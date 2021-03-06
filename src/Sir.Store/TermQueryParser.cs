﻿using System;
using System.Collections.Generic;
using System.Linq;

namespace Sir.Store
{
    /// <summary>
    /// Parses terms (key:value). Keys are separated from values by a ':' and
    /// terms are separated by newline characters. 
    /// Terms may be appended with a + sign (meaning AND), a - sign (meaning NOT) or nothing (meaning OR).
    /// </summary>
    public class TermQueryParser
    {
        private static  char[] Operators = new char[] { ' ', '+', '-' };

        public Query Parse(ulong collectionId, string query, ITokenizer tokenizer)
        {
            Query root = null;
            Query cursor = null;
            var lines = query
                .Replace("\r", "\n")
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);

            foreach (var line in lines)
            {
                if (line.IndexOf(':', 0, line.Length) < 0)
                {
                    throw new ArgumentException(
                        "Query syntax error. A query must define both a key and a value separated by a colon.", nameof(query));
                }

                var parts = line.Split(':');
                var key = parts[0];
                var value = parts[1];

                var values = key[0] == '_' ?
                    new AnalyzedString { Source = value.ToCharArray(), Tokens = new List<(int, int)> { (0, value.Length) } } :
                    tokenizer.Tokenize(value);

                values.Original = value;

                var or = key[0] != '+' && key[0] != '-';
                var not = key[0] == '-';
                var and = !or && !not;

                if (Operators.Contains(key[0]))
                {
                    key = key.Substring(1);
                }

                var q = new Query(collectionId, new Term(key, values, 0)) { And = and, Or = or, Not = not };
                var qc = q;

                for (int i = 1; i < values.Tokens.Count; i++)
                {
                    qc.Then = new Query(collectionId, new Term(key, values, i)) { And = and, Or = or, Not = not };
                    qc = qc.Then;
                }

                if (cursor == null)
                {
                    root = q;
                }
                else
                {
                    var last = cursor;
                    var next = last.Next;

                    while (next != null)
                    {
                        last = next;
                        next = last.Next;
                    }

                    last.Next = q;
                }

                cursor = q;
            }

            return root;
        }
    }
}
