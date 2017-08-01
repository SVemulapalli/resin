using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using log4net;
using Resin.Analysis;
using Resin.Querying;
using Resin.Sys;
using StreamIndex;
using DocumentTable;

namespace Resin
{
    /// <summary>
    /// Query a index.
    /// </summary>
    public class Searcher : IDisposable
    {
        private static readonly ILog Log = LogManager.GetLogger(typeof(Searcher));
        private readonly string _directory;
        private readonly QueryParser _parser;
        private readonly IScoringSchemeFactory _scorerFactory;
        private readonly long[] _versions;
        private readonly int _blockSize;
        private readonly IReadSessionFactory _sessionFactory;

        public Searcher(string directory)
            :this(directory, new QueryParser(new Analyzer()), new TfIdfFactory(), new FullTextReadSessionFactory(directory))
        {
        }

        public Searcher(string directory, long version)
            : this(directory, version, new QueryParser(new Analyzer()), new TfIdfFactory(), new FullTextReadSessionFactory(directory))
        {
        }

        public Searcher(string directory, QueryParser parser, IScoringSchemeFactory scorerFactory, IReadSessionFactory sessionFactory = null)
        {
            _directory = directory;
            _parser = parser;
            _scorerFactory = scorerFactory;
            _versions = Util.GetIndexFileNamesInChronologicalOrder(directory)
                .Select(f => long.Parse(Path.GetFileNameWithoutExtension(f))).ToArray();
            _blockSize = BlockSerializer.SizeOfBlock();
            _sessionFactory = sessionFactory ?? new FullTextReadSessionFactory(directory);
        }

        public Searcher(string directory, long version, QueryParser parser, IScoringSchemeFactory scorerFactory, IReadSessionFactory sessionFactory = null)
        {
            _directory = directory;
            _parser = parser;
            _scorerFactory = scorerFactory;
            _versions = new[] { version};
            _blockSize = BlockSerializer.SizeOfBlock();
            _sessionFactory = sessionFactory ?? new FullTextReadSessionFactory(directory);
        }

        public ScoredResult Search(IList<QueryContext> query, int page = 0, int size = 10000)
        {
            if (query == null)
            {
                return new ScoredResult { Docs = new List<ScoredDocument>() };
            }

            var searchTime = Stopwatch.StartNew();
            var skip = page * size;
            var scores = Collect(query);
            var paged = scores.OrderByDescending(s=>s.Score).Skip(skip).Take(size).ToArray();

            var docTime = Stopwatch.StartNew();
            var docs = new List<ScoredDocument>(size);
            var groupedByIx = paged.GroupBy(s => s.Ix);

            foreach (var group in groupedByIx)
            {
                GetDocs(group.ToList(), group.Key.VersionId, docs);
            }

            if (Log.IsDebugEnabled)
            {
                Log.DebugFormat("fetched {0} docs for query {1} in {2}", docs.Count, query.ToQueryString(), docTime.Elapsed);
            }

            var result = new ScoredResult
            {
                Total = scores.Length,
                Docs = docs.OrderByDescending(d => d.Score).ToList()
            };

            if (Log.IsDebugEnabled)
            {
                Log.DebugFormat("searched {0} in {1}", query.ToQueryString(), searchTime.Elapsed);
            }

            return result;
        }

        public ScoredResult Search(string query, int page = 0, int size = 10000)
        {
            var queryContext = _parser.Parse(query);

            return Search(queryContext, page, size);
        }

        private DocumentScore[] Collect(IList<QueryContext> query)
        {
            var scores = new List<DocumentScore[]>();

            foreach (var version in _versions)
            {
                using (var readSession = _sessionFactory.OpenReadSession(version))
                {
                    scores.Add(Collect(query, readSession));
                }
            }
            return scores.CombineTakingLatestVersion();
        }

        private DocumentScore[] Collect(IList<QueryContext> query, IReadSession readSession)
        {
            using (var collector = new Collector(_directory, readSession, _scorerFactory))
            {
                return collector.Collect(query);
            }
        }

        private void GetDocs(IList<DocumentScore> scores, long version, IList<ScoredDocument> result)
        {
            var documentIds = scores.Select(s => s.DocumentId).ToList();

            var dic = scores.ToDictionary(x => x.DocumentId, y => y.Score);

            using (var readSession = _sessionFactory.OpenReadSession(version))
            foreach (var doc in readSession.ReadDocuments(documentIds))
            {
                var score = dic[doc.Id];

                result.Add(new ScoredDocument(doc, score));
            }
        }

        public void Dispose()
        {
            ((IDisposable)_sessionFactory).Dispose();
        }
    }
}