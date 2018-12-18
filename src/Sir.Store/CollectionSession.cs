﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;

namespace Sir.Store
{
    public abstract class CollectionSession : IDisposable
    {
        protected SessionFactory SessionFactory { get; private set; }
        protected string CollectionId { get; }
        protected ConcurrentDictionary<long, IList<VectorNode>> Index { get; set; }
        protected Stream ValueStream { get; set; }
        protected Stream KeyStream { get; set; }
        protected Stream DocStream { get; set; }
        protected Stream ValueIndexStream { get; set; }
        protected Stream KeyIndexStream { get; set; }
        protected Stream DocIndexStream { get; set; }

        public CollectionSession(string collectionId, SessionFactory sessionFactory)
        {
            SessionFactory = sessionFactory;
            CollectionId = collectionId;
        }

        public virtual void Dispose()
        {
            if (ValueStream != null) ValueStream.Dispose();
            if (KeyStream != null) KeyStream.Dispose();
            if (DocStream != null) DocStream.Dispose();
            if (ValueIndexStream != null) ValueIndexStream.Dispose();
            if (KeyIndexStream != null) KeyIndexStream.Dispose();
            if (DocIndexStream != null) DocIndexStream.Dispose();
        }
    }
}