﻿#region Copyright 2014 by Benny Olsson, benny@unitednerds.se, Licensed under the Apache License, Version 2.0
/* Licensed under the Apache License, Version 2.0 (the "License");
 * you may not use this file except in compliance with the License.
 * You may obtain a copy of the License at
 * 
 *   http://www.apache.org/licenses/LICENSE-2.0
 * 
 * Unless required by applicable law or agreed to in writing, software
 * distributed under the License is distributed on an "AS IS" BASIS,
 * WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
 * See the License for the specific language governing permissions and
 * limitations under the License.
 */
#endregion
using System;
using System.IO;
using System.Diagnostics;


#pragma warning disable 169 // file contains unused fields. This is ok. 
#region File Layout Documentation
/*
 * A CompoundFile consists of multiple pages of data grouped in sections called Chapter. 
 * One Chapter holds 4096 pages and each page hold 4096 bytes of data. 
 * When a CompoundFile is first created it allocates one full chapter worth of disc space (16MB)
 * When the last page of that chapter is allocated the file is expanded and another chapter is allocated. 
 * 
 * All pages has a Page Link-field that points to the following page. 
 * If the page is a freepage then it points to the next freepage. If the page is allocated and part of a file
 * then it points to the next datapage. If the page is the last datapage for a file then the Page Link field is empty (0x0)
 * 
 * Every page ends with a 4 byte crc32 checksum that is calculated over index [0-4091] of the same page. 
 * The checksum is _always_ calculated so that in order to validate the integrity of a file you simple need to
 * calculate the crc for every page and validate them against the stored checksum. This goes for both freepages and datapages.
 * 
 * Page 0 of Chapter 0 contains the file header. 
 * The file header points to the first free page. This page is the one that will be allocated when Allocate() is called. 
 * When a page is allocated the file header page link will be updated to the page following the one it pointed to previously. 
 * 
 * 
Page layout
 [0-1] - status 
 [1-5] - linked page (links to next data page when page is in use, links to the next freepage when page is free)
 [5-9] - total data length (when several pages are linked, total data length indicates all remaining data from the current page and forward. 
 [9-4092] - data
 [4092-4095] - crc

Page 0, File header
 [0-50] Magic string
 [50-52] file version
 [52-54] page size
 [54-56] chapter size
 [56-60] first free page
 [4092-4095] crc
*/
#endregion

#region TODOs, notes and limitations
// todo         change PAGE_HEADER_LENGTH to 64bit
// todo         change PAGE_HEADER_LINK to 64bit
// todo         add more options to the page status field (see below)
// todo         improve read performance when VerifyOnRead is true by only reading the page once. 
// todo         implement a better locking strategy for reading. Simultaneous I/O and multiple read. 
// todo         implement a stream reader cache
// todo         implement a better locking strategy for writing. Write multiple pages in parallell, synchronize when chapter needs to be added.
// todo         implement asynchronous write (overlapped IO)
// todo         implement ReadAt(uint handle, long position, byte[] buffer, int offset, int count)
// todo         add a Minor File Version field to the file header. Minor version = non breaking changes. Major version = breaking changes.
// todo         add a status field to page header that indicates that the content exists but is unavailable (syncing)
// todo         look over what Exceptions are being thrown and see if they are "correct" or should be changed. 
// todo         implement a LRU page read cache.
// todo         implement Shrink() (Allow injection of an IShrinker/IPageMover/Similar since shrinking may require knowledge of content.)
// todo         implement Defrag() (Allow injection of IPageMover/Similar since defrag may require knowledge of content. e.g. a binary tree node stored in a page may hold page index to other nodes.) 
//
// page status flags (wishlist)
// isCompressed
// startOfFile
// free 
// readOnly
//
// if isCompressed == 1 and startOfFile == 0, throw exception when calling WriteAt
// if isCompressed == 1, all blocks that make up a file must be rewritten if the file is changed
//
// limitations
// currentnly, embedded files are only allowed to grow to <= 2GB. (remember to change this note when PAGE_HEADER_LENGTH is changed!)
// actual file can grow to ~16TB (remember to change this note when PAGE_HEADER_LINK is changed)
/*
private enum PageInfoMask
{
    Free = 1,
    Compressed = 2,
    FirstPage = 4,
    ReadOnly = 8
    ...
}
*/
#endregion

namespace TinyFS
{
    public class CompoundFile : IDisposable
    {
        #region Constants
        private const uint NO_LINK_ID = 0;
        private const byte PAGE_STATUS_FREE = 1;
        private const byte PAGE_STATUS_ALLOCATED = 0;
        private const byte PAGE_HEADER_SIZE = 9;
        private const byte PAGE_FOOTER_SIZE = 4;

        private const byte FILE_HEADER_INDEX_MAGIC = 0;
        private const byte FILE_HEADER_INDEX_VERSION = 50;
        private const byte FILE_HEADER_INDEX_PAGESIZE = 52;
        private const byte FILE_HEADER_INDEX_CHAPTERSIZE = 54;
        private const byte FILE_HEADER_INDEX_FIRST_FREEPAGE = 60;

        private const byte PAGE_HEADER_INDEX_STATUS = 0;    // for future use
        private const byte PAGE_HEADER_INDEX_PAGE_LINK = 1;
        private const byte PAGE_HEADER_INDEX_DATA_LENGTH = 5;
        private const UInt16 PAGE_FOOTER_INDEX_CRC = PAGE_SIZE - 4;
        private const UInt16 PAGE_SIZE = 4096;
        private const UInt16 PAGE_DATA_SIZE = PAGE_SIZE - PAGE_HEADER_SIZE - PAGE_FOOTER_SIZE;
        private const UInt16 CHAPTER_SIZE = 4096;
        private const uint MAX_PAGE_COUNT = uint.MaxValue;
        private const string MAGIC_STRING = "UNICORNS 4-LIFE";
        private const UInt16 FILE_VERSION = 1;
        private const uint FILE_HEADER_PAGE_INDEX = 0;

        private static readonly byte[] UINT_ZERO = new byte[]{0x0, 0x0, 0x0, 0x0};
        #endregion

        private Stream _stream;
        private readonly object _sync = new object();
        private readonly FileHeader _header = new FileHeader();
        private readonly CompoundFileOptions _options;
        private bool _disposed;

        public CompoundFile(string path) : this(path, new CompoundFileOptions()) { }
        public CompoundFile(string path, CompoundFileOptions options) : this(path, options, new FileStreamFactory()) { }

        public CompoundFile(string path, CompoundFileOptions options, IFileStreamFactory fileStreamFactory)
        {
            Debug.Assert(UINT_ZERO.Length == sizeof(uint));
            FileOptions foptions;
            if (options.UseWriteCache)
                foptions = FileOptions.RandomAccess;
            else
                foptions = FileOptions.RandomAccess | FileOptions.WriteThrough;
            _options = options;
            bool fileExists = File.Exists(path);
            lock (_sync)
            {
                _stream = fileStreamFactory.Create(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.Read, options.BufferSize, foptions);
                if (!TryLoadFileHeader())
                {
                    if (fileExists)
                    {
                        if (_header.Version < FILE_VERSION) throw new InvalidDataException("File version is not supported");
                        if (!new System.Text.ASCIIEncoding().GetString(_header.Magic).Equals(MAGIC_STRING)) throw new InvalidDataException("Corrupt file");
                        throw new Exception("Failed to open file. Possible corrupt file");
                    }
                    InitializeFile();
                }            
            }
        }

        // one of the few exceptions to the "don't ever, ever write your own finalizer!"-rule. 
        ~CompoundFile()
        {
            Dispose(false);
        }

        public uint Allocate()
        {
            lock(_sync)
            {
                var ix = _header.FirstFreePage;
                Debug.Assert(ix > 0);
                var header = ReadPageHeader(ix);
                var freepageIx = GetFreePageIndexFromPageHeader(header);
                if (freepageIx == 0)
                {
                    AddChapter();
                    freepageIx = (_header.ChapterCount - 1) * CHAPTER_SIZE;
                }
                _header.FirstFreePage = freepageIx;
                header[0] = PAGE_STATUS_ALLOCATED;
                Buffer.BlockCopy(BitConverter.GetBytes(NO_LINK_ID), 0, header, 1, sizeof (uint));
                WritePageHeader(ix, header);
                WriteFileHeader();
                if (_options.FlushAtWrite) Flush(true);
                return ix;
            }
        }

        public uint Allocate(uint size)
        {
            lock(_sync)
            {
                var ret = Allocate();
                size -= size > PAGE_DATA_SIZE ? PAGE_DATA_SIZE : size;
                var ixa = ret;
                while (size > 0)
                {
                    var ixb = Allocate();
                    size -= size > PAGE_DATA_SIZE ? PAGE_DATA_SIZE : size;
                    WritePageLink(ixa, ixb);
                    ixa = ixb;
                }
                if (_options.FlushAtWrite) Flush(true);
                return ret;                
            }
        }

        public uint Allocate(uint index, uint size)
        {
            var orgindex = index;
            lock(_sync)
            {
                var header = ReadPageHeader(index);
                if (header[PAGE_HEADER_INDEX_STATUS] != PAGE_STATUS_FREE) throw new IOException("page is not free");

                // special handle if index = first freepage
                if (_header.FirstFreePage == index)
                {
                    _header.FirstFreePage = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_PAGE_LINK);
                    WriteFileHeader();
                }
                else
                {
                    // find the freepage that is pointing to this page
                    var freeIx = _header.FirstFreePage;
                    while (true)
                    {
                        var freeHeader = ReadPageHeader(freeIx);
                        if (BitConverter.ToUInt32(freeHeader, PAGE_HEADER_INDEX_PAGE_LINK) == index)
                        {
                            // point that freepage to the freepage that this page is pointing to
                            Buffer.BlockCopy(header, PAGE_HEADER_INDEX_PAGE_LINK, freeHeader, PAGE_HEADER_INDEX_PAGE_LINK, sizeof(uint));
                            WritePageHeader(freeIx, freeHeader);
                            WritePageCrc(freeIx);
                            break;
                        }
                        freeIx = BitConverter.ToUInt32(freeHeader, PAGE_HEADER_INDEX_PAGE_LINK);
                        if (freeIx == NO_LINK_ID) throw new IOException("page not found in freepage list");
                    }
                }

                var prevIx = _header.FirstFreePage;
                while (true)
                {
                    if (prevIx == index) break;

                }
                header[PAGE_HEADER_INDEX_PAGE_LINK] = PAGE_STATUS_ALLOCATED;
                Buffer.BlockCopy(BitConverter.GetBytes(size), 0, header, PAGE_HEADER_SIZE, sizeof(uint));
                WritePageHeader(index, header);
                // set page header status

                size = size > PAGE_DATA_SIZE ? size - PAGE_DATA_SIZE : 0;
                // allocate and link pages as long as size > 0
                while (size > 0)
                {
                    var ix = Allocate();
                    header = ReadPageHeader(ix);
                    Buffer.BlockCopy(BitConverter.GetBytes(size), 0, header, PAGE_HEADER_SIZE, sizeof(uint));
                    WritePageHeader(ix, header);
                    WritePageLink(index, ix);
                    WritePageCrc(index);
                    index = ix;
                    size = size > PAGE_DATA_SIZE ? size - PAGE_DATA_SIZE : 0;
                }
                WritePageLink(index, NO_LINK_ID);
                WritePageCrc(index);
                if (_options.FlushAtWrite) Flush(true);                
            }
            return orgindex;
        }

        public void Free(uint handle)
        {
            if (_stream == null) throw new ObjectDisposedException(GetType().FullName);
            if (_stream.Length < handle * PAGE_SIZE) throw new IndexOutOfRangeException("handle must refer to an actual handle in the file");
            if (handle == FILE_HEADER_PAGE_INDEX) throw new IOException("Invalid handle");
            uint orghandle = handle;
            lock(_sync)
            {
                while (handle != NO_LINK_ID)
                {
                    var header = ReadPageHeader(handle);
                    header[PAGE_HEADER_INDEX_STATUS] = PAGE_STATUS_FREE;
                    Buffer.BlockCopy(UINT_ZERO, 0, header, PAGE_HEADER_INDEX_DATA_LENGTH, sizeof(uint));
                    uint ix = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_PAGE_LINK);
                    if (ix == NO_LINK_ID)
                    {
                        Buffer.BlockCopy(BitConverter.GetBytes(_header.FirstFreePage), 0, header, PAGE_HEADER_INDEX_PAGE_LINK, sizeof(uint));
                    }
                    WritePageHeader(handle, header);
                    WritePageCrc(handle);
                    handle = ix;
                }
                _header.FirstFreePage = orghandle;
                WriteFileHeader();
                if (_options.FlushAtWrite) Flush(true);
            }
        }

        public void Write(uint handle, byte[] data, int offset, int count)
        {
            if (data.Length < offset + count) throw new IndexOutOfRangeException();
            if (_stream == null) throw new ObjectDisposedException(GetType().FullName);
            if (_stream.Length < handle * PAGE_SIZE) throw new IndexOutOfRangeException();
            if (handle == FILE_HEADER_PAGE_INDEX) throw new IOException("Invalid handle");
            var page = new byte[PAGE_DATA_SIZE];
            lock(_sync)
            {
                byte[] header;
                while (count > 0)
                {
                    header = ReadPageHeader(handle);
                    Buffer.BlockCopy(BitConverter.GetBytes((uint)count), 0, header, PAGE_HEADER_INDEX_DATA_LENGTH, sizeof(uint));
                    WritePageHeader(handle, header);
                    int ic = count > PAGE_DATA_SIZE ? PAGE_DATA_SIZE : count;
                    Buffer.BlockCopy(data, offset, page, 0, ic);
                    _stream.Position = (handle * PAGE_SIZE) + PAGE_HEADER_SIZE;
                    _stream.Write(page, 0, ic);
                    offset += ic;
                    count -= ic;
                    if (count > 0)
                    {
                        if (BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_PAGE_LINK) == NO_LINK_ID)
                        {
                            uint ix = Allocate();
                            WritePageLink(handle, ix);
                            WritePageCrc(handle);
                            handle = ix;                            
                        } else
                        {
                            uint ix = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_PAGE_LINK);
                            WritePageCrc(handle);
                            handle = ix;
                        }
                    } else
                    {
                        WritePageCrc(handle);                        
                    }
                }
                header = ReadPageHeader(handle);
                if (BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_PAGE_LINK) != NO_LINK_ID)
                {
                    var nextPage = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_PAGE_LINK);
                    Buffer.BlockCopy(UINT_ZERO, 0, header, PAGE_HEADER_INDEX_PAGE_LINK, sizeof (uint));
                    WritePageHeader(handle, header);
                    WritePageCrc(handle);
                    Free(nextPage);
                }
                if (_options.FlushAtWrite) Flush(true);
            }
        }

        public void WriteAt(uint handle, uint position, byte[] data, int offset, int count)
        {
            if (data.Length < offset + count) throw new IndexOutOfRangeException();
            if (_stream == null) throw new ObjectDisposedException(GetType().FullName);
            if (_stream.Length < handle * PAGE_SIZE) throw new IndexOutOfRangeException();
            if (handle == FILE_HEADER_PAGE_INDEX) throw new IOException("Invalid handle");

            lock(_sync)
            {
                // calculate new total size and write to header
                var header = ReadPageHeader(handle);
                uint length = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_DATA_LENGTH);
                length = position + count > length ? position + (uint)count : length;
                Buffer.BlockCopy(BitConverter.GetBytes(length), 0, header, PAGE_HEADER_INDEX_DATA_LENGTH, sizeof(uint));
                WritePageHeader(handle, header);
                WritePageCrc(handle);
                // figure out which page is the first to write to 
                var ix = handle + (position / PAGE_SIZE);
                position -= ix * PAGE_SIZE;
                handle = ix;
                length -= ix * PAGE_DATA_SIZE;
                while (count > 0)
                {
                    // write remaining byte count to current header
                    header = ReadPageHeader(handle);
                    Buffer.BlockCopy(BitConverter.GetBytes(length), 0, header, PAGE_HEADER_INDEX_DATA_LENGTH, sizeof(uint));
                    WritePageHeader(handle, header);
                    // count may be bigger than capacity of current page
                    int ic = count > PAGE_DATA_SIZE ? PAGE_DATA_SIZE : count;
                    // decrease the amount of data to write by the position at which to start writing
                    ic -= (int)position;
                    var buffer = new byte[PAGE_DATA_SIZE - position];
                    Buffer.BlockCopy(data, offset, buffer, 0, ic);
                    _stream.Position = handle * PAGE_SIZE + PAGE_HEADER_SIZE + position;
                    _stream.Write(buffer, 0, ic);
                    count -= ic;
                    offset += ic;
                    length -= (uint)ic;
                    position = 0;
                    if (count > 0)
                    {
                        _stream.Position = (handle*PAGE_SIZE) + PAGE_HEADER_INDEX_PAGE_LINK;
                        _stream.Read(buffer, 0, 4);
                        handle = BitConverter.ToUInt32(buffer, 0);
                        // is this the last page of the current file layout? If so, allocate a new page and link it!
                        if (handle == NO_LINK_ID)
                        {
                            ix = Allocate();
                            WritePageLink(handle, ix);
                            WritePageCrc(handle);
                            handle = ix;
                        }
                    }
                    else
                    {
                        WritePageCrc(handle);
                    }
                }                
                if (_options.FlushAtWrite) Flush(true);
            }
        }

        // not the most optimal as this could potentially return a 4 GB byte array.
        // add support for Read(handle, dstbuffer, dstoffset, count, srcOffset)
        public byte[] ReadAll(uint handle)
        {
            if (_stream == null) throw new ObjectDisposedException(GetType().FullName);
            if (_stream.Length < handle * PAGE_SIZE) throw new IndexOutOfRangeException();
            if (handle == FILE_HEADER_PAGE_INDEX) throw new IOException("Invalid handle");
            var header = ReadPageHeader(handle);
            if (header[PAGE_HEADER_INDEX_STATUS] == PAGE_STATUS_FREE) throw new IOException("handle is unallocated");
            uint count = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_DATA_LENGTH);
            if (count > int.MaxValue) throw new InvalidOperationException();
            var data = new byte[count];
            uint offset = 0;
            lock(_sync)
            {
                while (count > 0)
                {
                    if (_options.VerifyOnRead && !ValidatePageCrc(handle)) throw new InvalidDataException("Checksum verification failed. Corrupt data.");
                    _stream.Position = handle * PAGE_SIZE + PAGE_HEADER_SIZE;
                    uint ic = count > PAGE_DATA_SIZE ? PAGE_DATA_SIZE : count;
                    _stream.Read(data, (int)offset, (int)ic);
                    offset += ic;
                    count -= ic;
                    if (count > 0)
                    {
                        var buffer = new byte[4];
                        _stream.Position = handle*PAGE_SIZE + PAGE_HEADER_INDEX_PAGE_LINK;
                        _stream.Read(buffer, 0, 4);
                        handle = BitConverter.ToUInt32(buffer, 0);
                    }
                }                
            }
            return data;
        }

        public uint ReadAt(uint handle, byte[] buffer, uint srcOffset, uint count)
        {
            var header = ReadPageHeader(handle);
            if (header[PAGE_HEADER_INDEX_STATUS] != PAGE_STATUS_ALLOCATED) throw new IOException("invalid handle");
            if (buffer.Length < count) throw new Exception("buffer not large enough for the data requested.");
            var size = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_DATA_LENGTH);
            if (size < srcOffset) throw new IOException("index out of range");
            count = size < srcOffset + count ? size - srcOffset : count;
            uint pageCount = srcOffset/PAGE_DATA_SIZE;
            lock(_sync)
            {
                while (pageCount > 0)
                {
                    header = ReadPageHeader(handle);
                    handle = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_PAGE_LINK);
                    pageCount--;
                }
                uint totalReadBytes = 0;
                while (count > 0)
                {
                    uint bytesToRead = PAGE_DATA_SIZE - srcOffset > count ? count : PAGE_DATA_SIZE - srcOffset;
                    _stream.Position = handle * PAGE_SIZE + PAGE_HEADER_SIZE + srcOffset;
                    var actualReadBytes = (uint)_stream.Read(buffer, (int)totalReadBytes, (int)bytesToRead);
                    count -= actualReadBytes;
                    if (actualReadBytes != bytesToRead && count > 0)
                    {
                        srcOffset += actualReadBytes;
                    }
                    else
                    {
                        srcOffset = 0;
                        header = ReadPageHeader(handle);
                        handle = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_PAGE_LINK);
                    }
                    totalReadBytes += actualReadBytes;
                }
                return totalReadBytes;                
            }
        }

        public uint GetLength(uint handle)
        {
            lock(_sync)
            {
                var header = ReadPageHeader(handle);
                return BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_DATA_LENGTH);
            }
        }

        /*
        private byte[] ReadPage(uint ix)
        {
            if (_stream == null) throw new ObjectDisposedException(GetType().FullName);
            if (_stream.Length < ix * PAGE_SIZE) throw new IndexOutOfRangeException();

            var header = ReadPageHeader(ix);
            uint count = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_DATA_LENGTH);
            count = count > PAGE_DATA_SIZE ? PAGE_DATA_SIZE : count;
            var buffer = new byte[count];
            uint position = ix*PAGE_SIZE;
            int ic = 0;
            lock(_sync)
            {
                _stream.Position = position;
                ic = _stream.Read(buffer, 0, (int) count);
            }
            if (ic != count)
            {
                var tmp = new byte[ic];
                Buffer.BlockCopy(buffer, 0, tmp, 0, ic);
                buffer = tmp;
            }
            return buffer;
        }
        */

        public bool ValidateCrc()
        {
            for (uint ix = 0; ix < _header.ChapterCount * CHAPTER_SIZE; ix++)
                if (!ValidatePageCrc(ix)) return false;
            return true;
        }

        private void WritePageLink(uint ix, uint linkIx)
        {
            _stream.Position = (ix*PAGE_SIZE) + PAGE_HEADER_INDEX_PAGE_LINK;
            _stream.Write(BitConverter.GetBytes(linkIx), 0, sizeof(uint));
        }

        private void WritePageCrc(uint ix)
        {
            _stream.Position = ix*PAGE_SIZE;
            var data = new byte[PAGE_SIZE];
            _stream.Read(data, 0, PAGE_SIZE);
            var crc = Win32Crc.GetCrc(data, 0, PAGE_SIZE - 4);
            _stream.Position = (ix*PAGE_SIZE) + PAGE_FOOTER_INDEX_CRC;
            _stream.Write(BitConverter.GetBytes(crc), 0, sizeof(uint));
        }

        private void WritePageHeader(uint ix, byte[] data)
        {
            Debug.Assert(data.Length == PAGE_HEADER_SIZE);
            var pos = ix*PAGE_SIZE;
            _stream.Position = pos;
            _stream.Write(data, 0, PAGE_HEADER_SIZE);
        }

        private byte[] ReadPageHeader(uint ix)
        {
            var pos = ix*PAGE_SIZE;
            Debug.Assert(_stream.Length > pos);
            _stream.Position = pos;
            var data = new byte[PAGE_HEADER_SIZE];
            _stream.Read(data, 0, PAGE_HEADER_SIZE);
            return data;
        }

        private uint GetFreePageIndexFromPageHeader(byte[] header)
        {
            Debug.Assert(header.Length == PAGE_HEADER_SIZE);
            var pageNumber = BitConverter.ToUInt32(header, PAGE_HEADER_INDEX_PAGE_LINK);
            return pageNumber;
        }

        private bool ValidatePageCrc(uint ix)
        {
            byte[] data = new byte[PAGE_SIZE-4];
            byte[] pageCrc = new byte[4];
            lock (_sync)
            {
                _stream.Position = ix * PAGE_SIZE;
                _stream.Read(data, 0, PAGE_SIZE - 4);
                _stream.Read(pageCrc, 0, 4);
            }
            var actualCrc = BitConverter.GetBytes(Win32Crc.GetCrc(data, 0, PAGE_SIZE-4));
            for(int i=0;i<4;i++)
            {
                if (pageCrc[i] != actualCrc[i]) return false;
            }
            return true;
        }

        private bool TryLoadFileHeader()
        {
            if (_stream == null)
                throw new ObjectDisposedException(GetType().FullName);
            if (_stream.Length == 0) return false;
            if (_stream.Length < CHAPTER_SIZE * PAGE_SIZE) return false;
            if (!ValidatePageCrc(FILE_HEADER_PAGE_INDEX)) return false;

            var buffer = new byte[50];
            _stream.Position = FILE_HEADER_INDEX_MAGIC;
            int magicbytecount = 0;
            for (; magicbytecount < 50; magicbytecount++)
            {
                _stream.Read(buffer, magicbytecount, 1);
                if (buffer[magicbytecount] == 0x0) break;
            }
            _header.Magic = new byte[magicbytecount];
            Buffer.BlockCopy(buffer, 0, _header.Magic, 0, magicbytecount);
            var encoder = new System.Text.ASCIIEncoding();

            var magicstring = encoder.GetString(_header.Magic);
            if (!magicstring.Equals(MAGIC_STRING)) return false;

            _stream.Position = FILE_HEADER_INDEX_VERSION;
            _stream.Read(buffer, 0, 2);
            _header.Version = BitConverter.ToUInt16(buffer, 0);
            if (_header.Version > FILE_VERSION) return false;

            _stream.Position = FILE_HEADER_INDEX_FIRST_FREEPAGE;
            _stream.Read(buffer, 0, 4);
            _header.FirstFreePage = BitConverter.ToUInt32(buffer, 0);
            _header.ChapterCount = Convert.ToUInt32(_stream.Length)/(CHAPTER_SIZE*PAGE_SIZE);
            return true;
        }

        private void InitializeFile()
        {
            InitializeFileHeader();
            lock (_sync)
            {
                AddChapter();
                WriteFileHeader();
                if (_options.FlushAtWrite) Flush(true);
            }
        }

        private void InitializeFileHeader()
        {
            var encoder = new System.Text.ASCIIEncoding();
            _header.Magic = encoder.GetBytes(MAGIC_STRING);
            _header.FirstFreePage = 1;

        }

        private void WriteFileHeader()
        {
            if (_stream == null) throw new ObjectDisposedException(GetType().FullName);
            lock(_sync)
            {
                var data = new byte[PAGE_SIZE];
                uint firstFreePageIndex = _header.FirstFreePage;
                Buffer.BlockCopy(_header.Magic, 0, data, FILE_HEADER_INDEX_MAGIC, _header.Magic.Length);
                Buffer.BlockCopy(BitConverter.GetBytes(FILE_VERSION), 0, data, FILE_HEADER_INDEX_VERSION, sizeof(UInt16));
                Buffer.BlockCopy(BitConverter.GetBytes(PAGE_SIZE), 0, data, FILE_HEADER_INDEX_PAGESIZE, sizeof(UInt16));
                Buffer.BlockCopy(BitConverter.GetBytes(CHAPTER_SIZE), 0, data, FILE_HEADER_INDEX_CHAPTERSIZE, sizeof(UInt16));
                Buffer.BlockCopy(BitConverter.GetBytes(firstFreePageIndex), 0, data, FILE_HEADER_INDEX_FIRST_FREEPAGE, sizeof(uint));
                var crc = Win32Crc.GetCrc(data, 0, PAGE_SIZE - 4);
                Buffer.BlockCopy(BitConverter.GetBytes(crc), 0, data, PAGE_SIZE - 4, 4);
                _stream.Position = 0;
                _stream.Write(data, 0, data.Length);
                if (_options.FlushAtWrite) Flush(true);
            }
        }

        private void AddChapter()
        {
            // note: AddChapter increments _header.ChapterCount without writing the file header to the stream. 
            //       All places where AddChapter are called are taking care of this since they often affect the  
            //       file header as well. Micro-optimization. Keep. 
            if (_stream == null) throw new ObjectDisposedException(GetType().FullName);
            if ((_header.ChapterCount * CHAPTER_SIZE) + CHAPTER_SIZE > MAX_PAGE_COUNT) throw new Exception("file full. Can't allocate additional pages");
            lock(_sync)
            {
                var data = new byte[CHAPTER_SIZE * PAGE_SIZE];
                byte[] crc;
                for (uint i = 0; i < CHAPTER_SIZE; i++)
                {
                    data[PAGE_SIZE * i] = PAGE_STATUS_FREE;
                    uint nextFreePage = _header.ChapterCount*CHAPTER_SIZE + i + 1;
                    Buffer.BlockCopy(BitConverter.GetBytes(nextFreePage), 0, data, Convert.ToInt32(PAGE_SIZE * i + 1), sizeof(uint));
                    crc = BitConverter.GetBytes(Win32Crc.GetCrc(data, (int)(PAGE_SIZE*i), PAGE_SIZE - 4)); // calculate crc on all but the last 4 bytes as that is where we store the crc value
                    Buffer.BlockCopy(crc, 0, data, Convert.ToInt32(PAGE_SIZE*i + PAGE_FOOTER_INDEX_CRC), 4);
                }
                // last page has no page link. fix and update crc.
                Buffer.BlockCopy(UINT_ZERO, 0, data, (CHAPTER_SIZE - 1) * PAGE_SIZE + PAGE_HEADER_INDEX_PAGE_LINK, sizeof(uint));
                crc = BitConverter.GetBytes(Win32Crc.GetCrc(data, (CHAPTER_SIZE - 1) * PAGE_SIZE, PAGE_SIZE-4));
                Buffer.BlockCopy(crc, 0, data, (CHAPTER_SIZE - 1) * PAGE_SIZE + PAGE_FOOTER_INDEX_CRC, 4);
                // figure out the correct stream position for where to write our new chapter
                _stream.Position = _header.ChapterCount*CHAPTER_SIZE * PAGE_SIZE;
                _stream.Write(data, 0, data.Length);
                _header.ChapterCount++;
                if (_options.FlushAtWrite) Flush(true);
            }
        }

        private void Flush(bool force)
        {
            if (_stream == null) throw new ObjectDisposedException(GetType().FullName);
            if (force)
                Flush(_stream);
            else
                _stream.Flush();
        }

        private void Flush(Stream stream)
        {
            var fs = stream as FileStream;
            if (fs == null)
            {
                stream.Flush();
            }
            else
            {
                if (fs.SafeFileHandle == null) throw new ObjectDisposedException(GetType().FullName);
                if (!Win32.FlushFileBuffers(fs.SafeFileHandle.DangerousGetHandle()))
                    throw new System.ComponentModel.Win32Exception();
            }
        }

        #region IDisposable members
        public void Dispose()
        {
            Dispose(true);
            GC.SuppressFinalize(this);
        }

        private void Dispose(bool disposing)
        {
            // bug: calling Dispose(false) won't write the file header nor close/dispose the stream

            // todo: we need to manage a native handle to the underlying Stream so that the stream aren't disposed/closed unless
            // we've been allowed to write the file header and flushed it one last time. 
            // Use case: if whoever is using this library aren't using it within a Using-block or forgets to call Dispose()
            // then Dispose(false) will be called from the finalizer. Normally in this case the Stream will have been disposed
            // be the framework and we can't write the file header. Calling Dispose(false) should result in the file header 
            // being written and the underlying stream getting disposed. 
            // Is GC pinning useful for this? 
            if (_disposed) return;
            _disposed = true;
            if (disposing)
            {
                if (_stream != null)
                {
                    WriteFileHeader();
                    Flush(true);
                    _stream.Dispose();
                }
                _stream = null;
            }
        }
        #endregion

        #region FileHeader class
        private class FileHeader
        {
            public byte[] Magic = new byte[50];
            public UInt16 Version = FILE_VERSION;
            public UInt16 PageSize = PAGE_SIZE;         // for future use
            public UInt16 ChapterSize = CHAPTER_SIZE;   // for future use
            public uint FirstFreePage;
            public uint ChapterCount;
        }
        #endregion

        public class CompoundFileOptions
        {
            public bool VerifyOnRead { get; private set; }
            public bool UseWriteCache { get; set; }
            public bool FlushAtWrite { get; set; }
            public int BufferSize { get; set; }

            public CompoundFileOptions()
            {
                VerifyOnRead = false;
                UseWriteCache = true;
                FlushAtWrite = false;
                BufferSize = 4096;
            }
        }
    }
}
