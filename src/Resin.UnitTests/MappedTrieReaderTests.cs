﻿using System.IO;
using System.Linq;
using Resin.IO;
using Resin.IO.Read;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using System.Collections.Generic;
using System;
using System.Text;
using StreamIndex;
using DocumentTable;
using Resin;

namespace Tests
{
    [TestClass]
    public class MappedTrieReaderTests : Setup
    {
        [TestMethod]
        public void Can_find_within_range()
        {
            var fileName = Path.Combine(CreateDir(), "MappedTrieReaderTests.Can_find_within_range.tri");

            var tree = new LcrsTrie();
            tree.Add("ape");
            tree.Add("app");
            tree.Add("apple");
            tree.Add("banana");
            tree.Add("bananas");
            tree.Add("xanax");
            tree.Add("xxx");

            foreach (var node in tree.EndOfWordNodes())
            {
                node.PostingsAddress = new BlockInfo(long.MinValue, int.MinValue);
            }

            File.WriteAllText("Can_find_within_range.log", tree.Visualize(), Encoding.UTF8);

            tree.Serialize(fileName);

            IList<Word> words;

            using (var reader = new MappedTrieReader(fileName))
            {
                 words = reader.Range("azz", "xerox").ToList();
            }

            Assert.AreEqual(3, words.Count);
            Assert.AreEqual("banana", words[0].Value);
            Assert.AreEqual("bananas", words[1].Value);
            Assert.AreEqual("xanax", words[2].Value);
        }

        [TestMethod]
        public void Can_find_within_numeric_range()
        {
            var fileName = Path.Combine(CreateDir(), "MappedTrieReaderTests.Can_find_within_numeric_range.tri");

            var tree = new LcrsTrie();
            tree.Add("0000123");
            tree.Add("0000333");
            tree.Add("0012345");
            tree.Add("0100006");
            tree.Add("1000989");
            tree.Add("0077777");
            tree.Add("0000666");

            foreach (var node in tree.EndOfWordNodes())
            {
                node.PostingsAddress = new BlockInfo(long.MinValue, int.MinValue);
            }

            File.WriteAllText("Can_find_within_numeric_range.log", tree.Visualize(), Encoding.UTF8);

            tree.Serialize(fileName);

            IList<Word> words;

            using (var reader = new MappedTrieReader(fileName))
            {
                words = reader.Range("0000333", "0100006").ToList();
            }

            Assert.AreEqual(5, words.Count);
            Assert.AreEqual("0000333", words[0].Value);
            Assert.AreEqual("0000666", words[1].Value);
            Assert.AreEqual("0012345", words[2].Value);
            Assert.AreEqual("0077777", words[3].Value);
            Assert.AreEqual("0100006", words[4].Value);
        }

        [TestMethod]
        public void Can_find_near()
        {
            var fileName = Path.Combine(CreateDir(), "MappedTrieReaderTests.Can_find_near.tri");

            var tree = new LcrsTrie();

            tree.Add("bad");
            tree.Add("baby");
            tree.Add("b");
            tree.Add("bananas");
            tree.Add("bank");

            foreach (var node in tree.EndOfWordNodes())
            {
                node.PostingsAddress = new BlockInfo(long.MinValue, int.MinValue);
            }

            tree.Serialize(fileName);
            File.WriteAllText("Can_find_near.log", tree.Visualize(), Encoding.UTF8);

            using (var reader = new MappedTrieReader(fileName))
            {
                var near = reader.SemanticallyNear("ba", 1).Select(w => w.Value).ToList();

                Assert.AreEqual(2, near.Count);
                Assert.IsTrue(near.Contains("bad"));
                Assert.IsTrue(near.Contains("b"));
            }

            using (var reader = new MappedTrieReader(fileName))
            {
                var near = reader.SemanticallyNear("ba", 2).Select(w => w.Value).ToList();

                Assert.AreEqual(4, near.Count);
                Assert.IsTrue(near.Contains("b"));
                Assert.IsTrue(near.Contains("bad"));
                Assert.IsTrue(near.Contains("baby"));
                Assert.IsTrue(near.Contains("bank"));
            }

            using (var reader = new MappedTrieReader(fileName))
            {
                var near = reader.SemanticallyNear("ba", 0).Select(w => w.Value).ToList();

                Assert.AreEqual(0, near.Count);
            }
            
            using (var reader = new MappedTrieReader(fileName))
            {
                var near = reader.SemanticallyNear("ba", 6).Select(w => w.Value).ToList();

                Assert.AreEqual(5, near.Count);
                Assert.IsTrue(near.Contains("b"));
                Assert.IsTrue(near.Contains("bad"));
                Assert.IsTrue(near.Contains("baby"));
                Assert.IsTrue(near.Contains("bank"));
                Assert.IsTrue(near.Contains("bananas"));
            }

            using (var reader = new MappedTrieReader(fileName))
            {
                var near = reader.SemanticallyNear("bazy", 1).Select(w => w.Value).ToList();

                Assert.AreEqual(1, near.Count);
                Assert.IsTrue(near.Contains("baby"));
            }
            
            using (var reader = new MappedTrieReader(fileName))
            {
                var near = reader.SemanticallyNear("bazy", 3).Select(w => w.Value).ToList();

                Assert.AreEqual(4, near.Count);
                Assert.IsTrue(near.Contains("baby"));
                Assert.IsTrue(near.Contains("bank"));
                Assert.IsTrue(near.Contains("bad"));
                Assert.IsTrue(near.Contains("b"));
            }
        }

        [TestMethod]
        public void Can_find_prefixed()
        {
            var fileName = Path.Combine(CreateDir(), "MappedTrieReaderTests.Can_find_prefixed.tri");

            var tree = new LcrsTrie('\0', false);

            tree.Add("rambo");
            tree.Add("rambo");

            tree.Add("2");

            tree.Add("rocky");

            tree.Add("2");

            tree.Add("raiders");

            tree.Add("of");
            tree.Add("the");
            tree.Add("lost");
            tree.Add("ark");

            tree.Add("rain");

            tree.Add("man");

            foreach (var node in tree.EndOfWordNodes())
            {
                node.PostingsAddress = new BlockInfo(long.MinValue, int.MinValue);
            }

            tree.Serialize(fileName);

            var prefixed = new MappedTrieReader(fileName).StartsWith("ra").Select(w => w.Value).ToList();

            Assert.AreEqual(3, prefixed.Count);
            Assert.IsTrue(prefixed.Contains("rambo"));
            Assert.IsTrue(prefixed.Contains("raiders"));
            Assert.IsTrue(prefixed.Contains("rain"));
        }

        [TestMethod]
        public void Can_find_exact()
        {
            var fileName = Path.Combine(CreateDir(), "MappedTrieReaderTests.Can_find_exact.tri");

            var tree = new LcrsTrie('\0', false);
            tree.Add("xor");
            tree.Add("xxx");
            tree.Add("donkey");
            tree.Add("xavier");
            tree.Add("baby");
            tree.Add("dad");
            tree.Add("daddy");

            foreach (var node in tree.EndOfWordNodes())
            {
                node.PostingsAddress = new BlockInfo(long.MinValue, int.MinValue);
            }

            tree.Serialize(fileName);

            using (var reader = new MappedTrieReader(fileName))
            {
                Assert.IsTrue(reader.IsWord("xxx").Any());
            }
            using (var reader = new MappedTrieReader(fileName))
            {
                Assert.IsTrue(reader.IsWord("xxx").Any());
            }
            using (var reader = new MappedTrieReader(fileName))
            {
                Assert.IsTrue(reader.IsWord("baby").Any());
            }
            using (var reader = new MappedTrieReader(fileName))
            {
                Assert.IsTrue(reader.IsWord("xxx").Any());
            }
            using (var reader = new MappedTrieReader(fileName))
            {
                Assert.IsTrue(reader.IsWord("baby").Any());
            }
            using (var reader = new MappedTrieReader(fileName))
            {
                Assert.IsTrue(reader.IsWord("dad").Any());
            }
            using (var reader = new MappedTrieReader(fileName))
            {
                Assert.IsTrue(reader.IsWord("daddy").Any());
            }
        }

        [TestMethod]
        public void Can_deserialize_whole_file()
        {
            var dir = CreateDir();

            var fileName = Path.Combine(dir, "MappedTrieReaderTests.Can_deserialize_whole_file.tri");

            var tree = new LcrsTrie('\0', false);
            tree.Add("baby");
            tree.Add("bad");
            tree.Add("badness");
            tree.Add("bank");
            tree.Add("box");
            tree.Add("dad");
            tree.Add("dance");
            tree.Add("flower");
            tree.Add("flowers");
            tree.Add("globe");
            tree.Add("global");

            File.WriteAllText("Can_deserialize_whole_file_orig.log", tree.Visualize(), Encoding.UTF8);

            foreach (var node in tree.EndOfWordNodes())
            {
                node.PostingsAddress = new BlockInfo(long.MinValue, int.MinValue);
            }

            Assert.IsTrue(tree.IsWord("baby").Any());
            Assert.IsTrue(tree.IsWord("bad").Any());
            Assert.IsTrue(tree.IsWord("badness").Any());
            Assert.IsTrue(tree.IsWord("bank").Any());
            Assert.IsTrue(tree.IsWord("box").Any());
            Assert.IsTrue(tree.IsWord("dad").Any());
            Assert.IsTrue(tree.IsWord("dance").Any());
            Assert.IsTrue(tree.IsWord("flower").Any());
            Assert.IsTrue(tree.IsWord("flowers").Any());
            Assert.IsTrue(tree.IsWord("globe").Any());
            Assert.IsTrue(tree.IsWord("global").Any());

            tree.Serialize(fileName);

            var recreated = Serializer.DeserializeTrie(fileName);

            File.WriteAllText("Can_deserialize_whole_file_recreated.log", recreated.Visualize(), Encoding.UTF8);

            var result = recreated.IsWord("baby");

            Assert.IsTrue(result.Any());
            Assert.IsTrue(recreated.IsWord("bad").Any());
            Assert.IsTrue(recreated.IsWord("badness").Any());
            Assert.IsTrue(recreated.IsWord("bank").Any());
            Assert.IsTrue(recreated.IsWord("box").Any());
            Assert.IsTrue(recreated.IsWord("dad").Any());
            Assert.IsTrue(recreated.IsWord("dance").Any());
            Assert.IsTrue(recreated.IsWord("flower").Any());
            Assert.IsTrue(recreated.IsWord("flowers").Any());
            Assert.IsTrue(recreated.IsWord("globe").Any());
            Assert.IsTrue(recreated.IsWord("global").Any());
        }
    }
}