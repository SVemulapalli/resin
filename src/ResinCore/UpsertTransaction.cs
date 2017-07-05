using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using log4net;
using Resin.Analysis;
using Resin.IO;
using Resin.IO.Write;
using Resin.Sys;
using System.Diagnostics;
using System.Linq;
using DocumentTable;

namespace Resin
{
    public class UpsertTransaction : IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(UpsertTransaction));

        private readonly string _directory;
        private readonly IAnalyzer _analyzer;
        private readonly Compression _compression;
        private readonly DocumentStream _documents;
        private readonly IWriteSession _writeSession;
        private int _count;
        private bool _committed;
        private readonly PostingsWriter _postingsWriter;
        private readonly BatchInfo _ix;
        private readonly Stream _compoundFile;

        public UpsertTransaction(
            string directory, 
            IAnalyzer analyzer, 
            Compression compression, 
            DocumentStream documents, 
            IWriteSessionFactory storeWriterFactory = null)
        {
            _directory = directory;
            _analyzer = analyzer;
            _compression = compression;
            _documents = documents;

            var firstCommit = Util.GetIndexFileNamesInChronologicalOrder(_directory)
                .FirstOrDefault();

            if (firstCommit != null)
            {
                var ix = BatchInfo.Load(firstCommit);

                _count = ix.DocumentCount;
            }

            _ix = new BatchInfo
            {
                VersionId = Util.GetNextChronologicalFileId(),
                Compression = _compression,
                PrimaryKeyFieldName = documents.PrimaryKeyFieldName
            };

            var posFileName = Path.Combine(
                _directory, string.Format("{0}.{1}", _ix.VersionId, "pos"));

            var compoundFileName = Path.Combine(_directory, _ix.VersionId + ".rdb");
            _compoundFile = new FileStream(compoundFileName, FileMode.CreateNew);

            var factory = storeWriterFactory ?? new WriteSessionFactory(directory, _ix, _compression);

            _writeSession = factory.OpenWriteSession(_compoundFile);

            _postingsWriter = new PostingsWriter(
                new FileStream(posFileName, FileMode.CreateNew, FileAccess.ReadWrite));
        }

        public long Write()
        {
            if (_committed) return _ix.VersionId;

            var ts = new List<Task>();
            var trieBuilder = new TrieBuilder();
            var docTimer = Stopwatch.StartNew();

            foreach (var doc in _documents.ReadSource())
            {
                doc.Id = _count++;

                new DocumentUpsertOperation().Write(
                    doc,
                    _writeSession,
                    _analyzer,
                    trieBuilder);
            }

            Log.InfoFormat("stored {0} documents in {1}", _count+1, docTimer.Elapsed);

            var posTimer = Stopwatch.StartNew();

            var tries = trieBuilder.GetTries();

            foreach (var trie in tries)
            {
                foreach (var node in trie.Value.EndOfWordNodes())
                {
                    node.PostingsAddress = _postingsWriter.Write(node.Postings);
                }

                if (Log.IsDebugEnabled)
                {
                    foreach (var word in trie.Value.Words())
                    {
                        Log.DebugFormat("{0}\t{1}", word.Value, word.Count);
                    }
                }
            }

            Log.InfoFormat(
                "stored postings refs in trees and wrote postings file in {0}", 
                posTimer.Elapsed);

            var treeTimer = new Stopwatch();
            treeTimer.Start();

            SerializeTries(tries);

            _ix.PostingsOffset = _compoundFile.Position;
            _postingsWriter.Stream.Flush();
            _postingsWriter.Stream.Position = 0;
            _postingsWriter.Stream.CopyTo(_compoundFile);

            Log.InfoFormat("serialized trees in {0}", treeTimer.Elapsed);

            return _ix.VersionId;
        }

        private void SerializeTries(IDictionary<string, LcrsTrie> tries)
        {
            foreach(var t in tries)
            {
                var fileName = Path.Combine(
                    _directory, string.Format("{0}-{1}.tri", _ix.VersionId, t.Key));

                t.Value.Serialize(fileName);
            }
        }

        private IEnumerable<Document> ReadSource()
        {
            return _documents.ReadSource();
        }

        public void Commit()
        {
            if (_committed) return;

            _writeSession.Flush();

            _postingsWriter.Dispose();
            _compoundFile.Dispose();
            _writeSession.Dispose();

            var fileName = Path.Combine(_directory, _ix.VersionId + ".ix");

            _ix.DocumentCount = _count;
            _ix.Serialize(fileName);

            _committed = true;
        }

        public void Dispose()
        {
            if (!_committed) Commit();

            Util.ReleaseFileLock(_directory);
        }
    }
}