﻿using Sir.Core;
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;

namespace Sir.Store
{
    /// <summary>
    /// Indexing session targeting a single collection.
    /// </summary>
    public class IndexSession : CollectionSession, IDisposable, ILogger
    {
        private readonly IConfigurationProvider _config;
        private readonly ITokenizer _tokenizer;
        private readonly IDictionary<long, VectorNode> _dirty;
        private readonly Stream _vectorStream;
        private bool _flushed;
        private bool _flushing;
        private readonly ProducerConsumerQueue<(long docId, long keyId, AnalyzedString tokens)> _modelBuilder;
        private readonly ProducerConsumerQueue<(long keyId, long docId, AnalyzedString tokens)> _validator;
        private readonly bool _validate;
        private readonly ConcurrentDictionary<long, NodeReader> _indexReaders;

        public IndexSession(
            string collectionName,
            ulong collectionId,
            SessionFactory sessionFactory, 
            ITokenizer tokenizer,
            IConfigurationProvider config,
            ConcurrentDictionary<long, NodeReader> indexReaders) : base(collectionName, collectionId, sessionFactory)
        {
            _config = config;
            _tokenizer = tokenizer;
            _dirty = new ConcurrentDictionary<long, VectorNode>();
            _vectorStream = SessionFactory.CreateAppendStream(Path.Combine(SessionFactory.Dir, string.Format("{0}.vec", CollectionId)));

            var numThreads = int.Parse(_config.Get("write_thread_count"));

            _modelBuilder = new ProducerConsumerQueue<(long docId, long keyId, AnalyzedString tokens)>(BuildModel, numThreads);

            _validator = new ProducerConsumerQueue<(long keyId, long docId, AnalyzedString tokens)>(Validate, numThreads, startConsumingImmediately: false);

            _validate = bool.Parse(config.Get("validate_when_indexing"));
            _indexReaders = indexReaders;
        }

        public void EmbedTerms(IDictionary document)
        {
            Analyze(document);
        }

        /// <summary>
        /// Fields prefixed with "__" will not be analyzed.
        /// Fields prefixed with "_" will be analyzed as a single token.
        /// </summary>
        /// <param name="document"></param>
        private void Analyze(IDictionary document)
        {
            var docId = (long)document["__docid"];

            foreach (var obj in document.Keys)
            {
                var key = (string)obj;
                AnalyzedString tokens = null;

                if (!key.StartsWith("__"))
                {
                    var keyHash = key.ToHash();
                    var keyId = SessionFactory.GetKeyId(CollectionId, keyHash);
                    var val = (IComparable)document[key];
                    var str = val as string;

                    if (str == null || key[0] == '_')
                    {
                        var v = val.ToString();

                        if (!string.IsNullOrWhiteSpace(v))
                        {
                            tokens = new AnalyzedString { Source = v.ToCharArray(), Tokens = new List<(int, int)> { (0, v.Length) } };
                        }
                    }
                    else
                    {
                        tokens = _tokenizer.Tokenize(str);
                    }

                    if (tokens != null)
                    {
                        _modelBuilder.Enqueue((docId, keyId, tokens));
                    }

                    if (_validate)
                        _validator.Enqueue((keyId, docId, tokens));
                }
            }

            this.Log("analyzed document {0} ", docId);
        }

        private void BuildModel((long docId, long keyId, AnalyzedString tokens) item)
        {
            var ix = GetOrCreateIndex(item.keyId);

            foreach (var vector in item.tokens.Embeddings)
            {
                ix.Add(new VectorNode(vector, item.docId), VectorNode.TermIdenticalAngle, VectorNode.TermFoldAngle, _vectorStream);
            }
        }

        public void Flush()
        {
            if (_flushing || _flushed)
                return;

            _flushing = true;

            this.Log("waiting for model builder");

            using (_modelBuilder)
            {
                _modelBuilder.Join();
            }

            using (_vectorStream)
            {
                _vectorStream.Flush();
                _vectorStream.Close();
            }

            if (_validate)
            {
                this.Log("awaiting validation");

                using (_validator)
                {
                    _validator.Start();
                    _validator.Join();
                }
            }               

            var tasks = new Task[_dirty.Count];
            var taskId = 0;
            var columnWriters = new List<ColumnSerializer>();

            foreach (var column in _dirty)
            {
                var columnWriter = new ColumnSerializer(
                    CollectionId, column.Key, SessionFactory, new RemotePostingsWriter(_config, CollectionName));

                columnWriters.Add(columnWriter);

                tasks[taskId++] = columnWriter.SerializeColumnSegment(column.Value);
            }

            Task.WaitAll(tasks);

            foreach (var writer in columnWriters)
            {
                writer.Dispose();
            }

            foreach (var column in _dirty)
            {
                NodeReader staleReader;
                _indexReaders.TryRemove(column.Key, out staleReader);
            }

            _flushed = true;
            _flushing = false;

            this.Log(string.Format("***FLUSHED***"));
        }

        private void Validate((long keyId, long docId, AnalyzedString tokens) item)
        {
            if (item.keyId == 4 || item.keyId == 5)
            {
                var tree = GetOrCreateIndex(item.keyId);

                foreach (var vector in item.tokens.Embeddings)
                {
                    var hit = tree.ClosestMatch(new VectorNode(vector), VectorNode.TermFoldAngle);

                    if (hit.Score < VectorNode.TermIdenticalAngle)
                    {
                        throw new DataMisalignedException();
                    }

                    var valid = false;

                    foreach (var id in hit.Ids)
                    {
                        if (id == item.docId)
                        {
                            valid = true;
                            break;
                        }
                    }

                    if (!valid)
                    {
                        throw new DataMisalignedException();
                    }
                }
            }
        }

        private static readonly object _syncIndexAccess = new object();

        private VectorNode GetOrCreateIndex(long keyId)
        {
            VectorNode root;

            if (!_dirty.TryGetValue(keyId, out root))
            {
                lock (_syncIndexAccess)
                {
                    if (!_dirty.TryGetValue(keyId, out root))
                    {
                        root = new VectorNode();
                        _dirty.Add(keyId, root);
                    }
                }
            }

            return root;
        }

        public void Dispose()
        {
            Flush();
        }
    }
}