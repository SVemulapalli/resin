﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace Sir.Postings
{
    public class StreamRepository : IDisposable, ILogger
    {
        private readonly IConfigurationProvider _config;
        private const string DataFileNameFormat = "{0}.pos";
        private readonly IDictionary<(ulong, long), IList<ulong>> _cache;

        public StreamRepository(IConfigurationProvider config)
        {
            _config = config;
            _cache = new ConcurrentDictionary<(ulong, long), IList<ulong>>();
        }

        public async Task<Result> Reduce(ulong collectionId, IList<Query> query, int skip, int take)
        {
            var result = new Dictionary<ulong, float>();

            foreach (var cursor in query)
            {
                var docIdList = await ReadAndRefreshCache(collectionId, cursor.PostingsOffset);
                var docIds = docIdList.ToDictionary(docId => docId, score => cursor.Score);

                if (cursor.And)
                {
                    var aggregatedResult = new Dictionary<ulong, float>();

                    foreach (var doc in result)
                    {
                        float score;

                        if (docIds.TryGetValue(doc.Key, out score))
                        {
                            aggregatedResult[doc.Key] = score + doc.Value;
                        }
                    }

                    result = aggregatedResult;
                }
                else if (cursor.Not)
                {
                    foreach (var id in docIds.Keys)
                    {
                        result.Remove(id, out float _);
                    }
                }
                else // Or
                {
                    foreach (var id in docIds)
                    {
                        float score;

                        if (result.TryGetValue(id.Key, out score))
                        {
                            result[id.Key] = score + id.Value;
                        }
                        else
                        {
                            result.Add(id.Key, id.Value);
                        }
                    }
                }
            }

            var sortedByScore = result.ToList();
            sortedByScore.Sort(
                delegate (KeyValuePair<ulong, float> pair1,
                KeyValuePair<ulong, float> pair2)
                {
                    return pair1.Value.CompareTo(pair2.Value);
                }
            );

            if (take < 1)
            {
                take = sortedByScore.Count;
            }
            if (skip < 1)
            {
                skip = 0;
            }

            sortedByScore.Reverse();

            var window = sortedByScore.Skip(skip).Take(take);

            var stream = Serialize(window);

            return new Result { Data = stream, MediaType = "application/postings", Total = sortedByScore.Count };
        }

        public async Task<Result> Read(ulong collectionId, long offset, int skip, int take)
        {
            var key = (collectionId, offset);
            IList<ulong> result;

            if (!_cache.TryGetValue(key, out result))
            {
                result = await ReadFromDisk(collectionId, offset);
                _cache[key] = result;
            }

            if (take < 1)
            {
                take = result.Count;
            }
            if (skip < 1)
            {
                skip = 0;
            }

            var stream = Serialize(result.Skip(skip).Take(take).ToDictionary(x => x, y => 0f));

            return new Result { Data = stream, MediaType = "application/postings", Total = result.Count };
        }

        private async Task<IList<ulong>> ReadAndRefreshCache(ulong collectionId, long offset)
        {
            var key = (collectionId, offset);
            IList<ulong> result;

            if (!_cache.TryGetValue(key, out result))
            {
                result = await ReadFromDisk(collectionId, offset);
                _cache[key] = result;
            }

            return result;
        }

        private async Task<IList<ulong>> ReadFromDisk(ulong collectionId, long offset)
        {
            var timer = new Stopwatch();
            timer.Start();

            var result = new HashSet<ulong>();
            var pageCount = 0;

            using (var data = CreateReadableDataStream(collectionId))
            {
                data.Seek(offset, SeekOrigin.Begin);

                // We are now at the first page.
                // Each page starts with a header consisting of (a) count, (b) next page offset and (c) last page offset (long, long, long).
                // The rest of the page is data (ulong's).

                var lbuf = new byte[sizeof(long)];

                await data.ReadAsync(lbuf);

                var count = BitConverter.ToInt64(lbuf);

                await data.ReadAsync(lbuf);

                var nextPageOffset = BitConverter.ToInt64(lbuf);

                var pageLen = sizeof(long) + (count * sizeof(ulong));
                var pageBuf = new byte[pageLen];

                await data.ReadAsync(pageBuf);

                for (int i = 0; i < count; i++)
                {
                    var entryOffset = sizeof(long) + (i * sizeof(ulong));
                    var entry = BitConverter.ToUInt64(pageBuf, entryOffset);

                    if (!result.Add(entry))
                    {
                        throw new DataMisalignedException("first page is crap");
                    }
                }

                this.Log("reading first page took {0}", timer.Elapsed);

                while (nextPageOffset > -1)
                {
                    timer.Restart();

                    data.Seek(nextPageOffset, SeekOrigin.Begin);

                    await data.ReadAsync(lbuf);

                    count = BitConverter.ToInt64(lbuf);

                    await data.ReadAsync(lbuf);

                    nextPageOffset = BitConverter.ToInt64(lbuf);

                    pageLen = sizeof(long) + (count * sizeof(ulong));
                    var page = new byte[pageLen];

                    await data.ReadAsync(page);

                    for (int i = 0; i < count; i++)
                    {
                        var entryOffset = sizeof(long) + (i * sizeof(ulong));
                        var entry = BitConverter.ToUInt64(page, entryOffset);

                        if (!result.Add(entry))
                        {
                            throw new DataMisalignedException("page is crap");
                        }
                    }

                    this.Log("reading next page took {0}", timer.Elapsed);

                    pageCount++;
                }
            }

            this.Log("read {0} postings from {1} pages in {2}", result.Count, pageCount, timer.Elapsed);

            return result.ToList();
        }

        public MemoryStream Write(ulong collectionId, byte[] messageBuf)
        {
            var time = Stopwatch.StartNew();
            int read = 0;
            var lists = new List<byte[]>();
            var offsets = new List<long>();
            var lengths = new List<int>();

            // read first word of payload
            var payloadCount = BitConverter.ToInt32(messageBuf, 0);
            read = sizeof(int);

            // read list lengths
            for (int index = 0; index < payloadCount; index++)
            {
                var len = BitConverter.ToInt32(messageBuf, read);
                lengths.Add(len);
                read += sizeof(int);
            }

            // read offsets (IDs)
            for (int index = 0; index < payloadCount; index++)
            {
                var offset = BitConverter.ToInt64(messageBuf, read);
                offsets.Add(offset);
                read += sizeof(long);
            }

            // read lists
            for (int index = 0; index < lengths.Count; index++)
            {
                var size = lengths[index];
                var buf = new byte[size];
                Buffer.BlockCopy(messageBuf, read, buf, 0, size);
                read += size;
                lists.Add(buf);
            }

            if (lists.Count != payloadCount)
            {
                throw new DataMisalignedException();
            }

            this.Log("parsed payload in {0}", time.Elapsed);
            time.Restart();

            var listOffsets = new List<long>(lists.Count);

            // persist payload

            using (var data = CreateReadableWritableDataStream(collectionId))
            {
                for (int listIndex = 0; listIndex < lists.Count; listIndex++)
                {
                    var offset = offsets[listIndex];
                    var list = lists[listIndex];

                    // invalidate cache
                    var cacheKey = (collectionId, offset);
                    if (_cache.ContainsKey(cacheKey))
                    {
                        _cache.Remove(cacheKey);
                    }

                    if (offset < 0)
                    {
                        // Since this data does not have an ID we are going to append it
                        // and give it a new ID.

                        data.Seek(0, SeekOrigin.End);

                        // record new file location
                        offset = data.Position;

                        // write count to header of this page
                        data.Write(BitConverter.GetBytes(Convert.ToInt64(list.Length/sizeof(ulong))));

                        // write nextPageOffset
                        data.Write(BitConverter.GetBytes((long)-1));

                        // write lastPageOffset
                        data.Write(BitConverter.GetBytes(offset));

                        // write payload
                        data.Write(list);
                    }
                    else
                    {
                        // There is already at least one page with this ID.
                        // We need to find the offset of that page so that we can update its header,
                        // then write the data and record its offset,
                        // and then write that offset into the header of the first page.

                        var nextPageOffsetWordPosition = offset + sizeof(long);
                        long lastPageOffsetWordPosition = offset + (2 * sizeof(long));
                        var lbuf = new byte[sizeof(long)];

                        data.Seek(lastPageOffsetWordPosition, SeekOrigin.Begin);

                        data.Read(lbuf);

                        long lastPageOffset = BitConverter.ToInt64(lbuf);

                        if (lastPageOffset != offset)
                        {
                            nextPageOffsetWordPosition = lastPageOffset + sizeof(long);
                        }

                        data.Seek(0, SeekOrigin.End);

                        var newPageOffset = data.Position;

                        // write count to header of this page
                        data.Write(BitConverter.GetBytes(Convert.ToInt64(list.Length / sizeof(ulong))));

                        // write nextPageOffset
                        data.Write(BitConverter.GetBytes((long)-1));

                        // write lastPageOffset
                        data.Write(BitConverter.GetBytes((long)-1));

                        // write payload
                        data.Write(list);

                        // update next page offset
                        data.Seek(nextPageOffsetWordPosition, SeekOrigin.Begin);
                        data.Write(BitConverter.GetBytes(newPageOffset));

                        // update last page offset
                        data.Seek(lastPageOffsetWordPosition, SeekOrigin.Begin);
                        data.Write(BitConverter.GetBytes(newPageOffset));
                    }

                    listOffsets.Add(offset);
                }
            }

            this.Log("serialized data and index in {0}", time.Elapsed);

            time.Restart();

            if (listOffsets.Count != payloadCount)
            {
                throw new DataMisalignedException();
            }

            // construct a response message that contains a list of offsets (IDs).

            var res = new MemoryStream();

            for (int i = 0; i < listOffsets.Count; i++)
            {
                res.Write(BitConverter.GetBytes(listOffsets[i]));
            }

            res.Position = 0;

            this.Log("serialized response message in {0}", time.Elapsed);

            return res;
        }

        private MemoryStream Serialize(IEnumerable<KeyValuePair<ulong, float>> docs)
        {
            var timer = Stopwatch.StartNew();
            var result = new MemoryStream();

            foreach (var doc in docs)
            {
                result.Write(BitConverter.GetBytes(doc.Key));
                result.Write(BitConverter.GetBytes(doc.Value));
            }

            this.Log("serialized result in {0}", timer.Elapsed);

            return result;
        }
        
        private Stream CreateReadableWritableDataStream(ulong collectionId)
        {
            var fileName = Path.Combine(_config.Get("data_dir"), string.Format(DataFileNameFormat, collectionId));

            var stream = new FileStream(fileName, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.ReadWrite);

            return Stream.Synchronized(stream);
        }

        private Stream CreateReadableDataStream(ulong collectionId)
        {
            var fileName = Path.Combine(_config.Get("data_dir"), string.Format(DataFileNameFormat, collectionId));

            if (File.Exists(fileName))
            {
                var stream = new FileStream(fileName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, true);
                return stream;
            }

            return null;
        }

        public void Dispose()
        {
        }
    }
}
