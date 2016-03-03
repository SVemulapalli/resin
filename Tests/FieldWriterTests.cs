﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using NUnit.Framework;
using Resin;

namespace Tests
{
    [TestFixture]
    public class FieldWriterTests
    {
        [Test]
        public void Can_create_field_file()
        {
            const string fileName = "c:\\temp\\resin_tests\\Can_create_field_file\\0.fld";
            if (File.Exists(fileName)) File.Delete(fileName);
            using (var fw = new FieldWriter(fileName))
            {
                fw.Add(0, "hello", 0);
                fw.Add(0, "world", 1);
            }
            Assert.IsTrue(File.Exists(fileName));
        }
    }
}
