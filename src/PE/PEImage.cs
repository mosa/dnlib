/*
    Copyright (C) 2012-2014 de4dot@gmail.com

    Permission is hereby granted, free of charge, to any person obtaining
    a copy of this software and associated documentation files (the
    "Software"), to deal in the Software without restriction, including
    without limitation the rights to use, copy, modify, merge, publish,
    distribute, sublicense, and/or sell copies of the Software, and to
    permit persons to whom the Software is furnished to do so, subject to
    the following conditions:

    The above copyright notice and this permission notice shall be
    included in all copies or substantial portions of the Software.

    THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND,
    EXPRESS OR IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF
    MERCHANTABILITY, FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT.
    IN NO EVENT SHALL THE AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY
    CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER IN AN ACTION OF CONTRACT,
    TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN CONNECTION WITH THE
    SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
*/

﻿using System;
using System.Collections.Generic;
using System.IO;
using dnlib.Utils;
using dnlib.W32Resources;
using dnlib.IO;
using dnlib.Threading;

namespace dnlib.PE {
	/// <summary>
	/// Image layout
	/// </summary>
	public enum ImageLayout {
		/// <summary>
		/// Use this if the PE file has a normal structure (eg. it's been read from a file on disk)
		/// </summary>
		File,

		/// <summary>
		/// Use this if the PE file has been loaded into memory by the OS PE file loader
		/// </summary>
		Memory,
	}

	/// <summary>
	/// Accesses a PE file
	/// </summary>
	public sealed class PEImage : IPEImage {
		// Default to false because an OS loaded PE image may contain memory holes. If there
		// are memory holes, other code (eg. .NET resource creator) must verify that all memory
		// is available, which will be slower.
		const bool USE_MEMORY_LAYOUT_WITH_MAPPED_FILES = false;

		// Current Memory-mapped files implementation uses Win32 API, thus does not work on
		// Unix-like OS. For those platforms, use MemoryStreamCreator instead.
		static readonly bool IS_UNIX;

		static PEImage()
		{
			// See http://mono-project.com/FAQ:_Technical#Mono_Platforms for platform detection.
			int p = (int)Environment.OSVersion.Platform;
			if (p == 4 || p == 6 || p == 128)
				IS_UNIX = true;
		}

		static readonly IPEType MemoryLayout = new MemoryPEType();
		static readonly IPEType FileLayout = new FilePEType();

		IImageStream imageStream;
		IImageStreamCreator imageStreamCreator;
		IPEType peType;
		PEInfo peInfo;
		UserValue<Win32Resources> win32Resources;
#if THREAD_SAFE
		readonly Lock theLock = Lock.Create();
#endif

		sealed class FilePEType : IPEType {
			/// <inheritdoc/>
			public RVA ToRVA(PEInfo peInfo, FileOffset offset) {
				return peInfo.ToRVA(offset);
			}

			/// <inheritdoc/>
			public FileOffset ToFileOffset(PEInfo peInfo, RVA rva) {
				return peInfo.ToFileOffset(rva);
			}
		}

		sealed class MemoryPEType : IPEType {
			/// <inheritdoc/>
			public RVA ToRVA(PEInfo peInfo, FileOffset offset) {
				return (RVA)offset;
			}

			/// <inheritdoc/>
			public FileOffset ToFileOffset(PEInfo peInfo, RVA rva) {
				return (FileOffset)rva;
			}
		}

		/// <inheritdoc/>
		public bool IsFileImageLayout {
			get { return peType is FilePEType; }
		}

		/// <inheritdoc/>
		public bool MayHaveInvalidAddresses {
			get { return !IsFileImageLayout; }
		}

		/// <inheritdoc/>
		public string FileName {
			get { return imageStreamCreator.FileName; }
		}

		/// <inheritdoc/>
		public ImageDosHeader ImageDosHeader {
			get { return peInfo.ImageDosHeader; }
		}

		/// <inheritdoc/>
		public ImageNTHeaders ImageNTHeaders {
			get { return peInfo.ImageNTHeaders; }
		}

		/// <inheritdoc/>
		public IList<ImageSectionHeader> ImageSectionHeaders {
			get { return peInfo.ImageSectionHeaders; }
		}

		/// <inheritdoc/>
		public Win32Resources Win32Resources {
			get { return win32Resources.Value; }
			set {
				IDisposable origValue = null;
				if (win32Resources.IsValueInitialized) {
					origValue = win32Resources.Value;
					if (origValue == value)
						return;
				}
				win32Resources.Value = value;

				if (origValue != null)
					origValue.Dispose();
			}
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="imageStreamCreator">The PE stream creator</param>
		/// <param name="imageLayout">Image layout</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(IImageStreamCreator imageStreamCreator, ImageLayout imageLayout, bool verify) {
			try {
				this.imageStreamCreator = imageStreamCreator;
				this.peType = ConvertImageLayout(imageLayout);
				ResetReader();
				this.peInfo = new PEInfo(imageStream, verify);
				Initialize();
			}
			catch {
				Dispose();
				throw;
			}
		}

		void Initialize() {
			win32Resources.ReadOriginalValue = () => {
				var dataDir = peInfo.ImageNTHeaders.OptionalHeader.DataDirectories[2];
				if (dataDir.VirtualAddress == 0 || dataDir.Size == 0)
					return null;
				return new Win32ResourcesPE(this);
			};
#if THREAD_SAFE
			win32Resources.Lock = theLock;
#endif
		}

		static IPEType ConvertImageLayout(ImageLayout imageLayout) {
			switch (imageLayout) {
			case ImageLayout.File: return FileLayout;
			case ImageLayout.Memory: return MemoryLayout;
			default: throw new ArgumentException("imageLayout");
			}
		}

		static IImageStreamCreator GetFileStreamCreator(string fileName, bool mapAsImage)
		{
			if (IS_UNIX)
				return new MemoryStreamCreator(File.ReadAllBytes(fileName)) { FileName = fileName };
			else
				return new MemoryMappedFileStreamCreator(fileName, mapAsImage);
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="fileName">Name of the file</param>
		/// <param name="mapAsImage"><c>true</c> if we should map it as an executable</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(string fileName, bool mapAsImage, bool verify)
			: this(GetFileStreamCreator(fileName, mapAsImage), mapAsImage ? ImageLayout.Memory : ImageLayout.File, verify) {
			try {
				if (IS_UNIX && mapAsImage) {
					((MemoryMappedFileStreamCreator)imageStreamCreator).Length = peInfo.GetImageSize();
					ResetReader();
				}
			}
			catch {
				Dispose();
				throw;
			}
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="fileName">Name of the file</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(string fileName, bool verify)
			: this(fileName, USE_MEMORY_LAYOUT_WITH_MAPPED_FILES, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="fileName">Name of the file</param>
		public PEImage(string fileName)
			: this(fileName, true) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="data">The PE file data</param>
		/// <param name="imageLayout">Image layout</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(byte[] data, ImageLayout imageLayout, bool verify)
			: this(new MemoryStreamCreator(data), imageLayout, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="data">The PE file data</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(byte[] data, bool verify)
			: this(data, ImageLayout.File, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="data">The PE file data</param>
		public PEImage(byte[] data)
			: this(data, true) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		/// <param name="length">Length of PE image</param>
		/// <param name="imageLayout">Image layout</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(IntPtr baseAddr, long length, ImageLayout imageLayout, bool verify)
			: this(new UnmanagedMemoryStreamCreator(baseAddr, length), imageLayout, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		/// <param name="length">Length of PE image</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(IntPtr baseAddr, long length, bool verify)
			: this(baseAddr, length, ImageLayout.Memory, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		/// <param name="length">Length of PE image</param>
		public PEImage(IntPtr baseAddr, long length)
			: this(baseAddr, length, true) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		/// <param name="imageLayout">Image layout</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(IntPtr baseAddr, ImageLayout imageLayout, bool verify)
			: this(new UnmanagedMemoryStreamCreator(baseAddr, 0x10000), imageLayout, verify) {
			try {
				((UnmanagedMemoryStreamCreator)imageStreamCreator).Length = peInfo.GetImageSize();
				ResetReader();
			}
			catch {
				Dispose();
				throw;
			}
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		/// <param name="verify">Verify PE file data</param>
		public PEImage(IntPtr baseAddr, bool verify)
			: this(baseAddr, ImageLayout.Memory, verify) {
		}

		/// <summary>
		/// Constructor
		/// </summary>
		/// <param name="baseAddr">Address of PE image</param>
		public PEImage(IntPtr baseAddr)
			: this(baseAddr, true) {
		}

		void ResetReader() {
			if (imageStream != null) {
				imageStream.Dispose();
				imageStream = null;
			}
			imageStream = imageStreamCreator.CreateFull();
		}

		/// <inheritdoc/>
		public RVA ToRVA(FileOffset offset) {
			return peType.ToRVA(peInfo, offset);
		}

		/// <inheritdoc/>
		public FileOffset ToFileOffset(RVA rva) {
			return peType.ToFileOffset(peInfo, rva);
		}

		/// <inheritdoc/>
		public void Dispose() {
			IDisposable id;
			if (win32Resources.IsValueInitialized && (id = win32Resources.Value) != null)
				id.Dispose();
			if ((id = imageStream) != null)
				id.Dispose();
			if ((id = imageStreamCreator) != null)
				id.Dispose();
			win32Resources.Value = null;
			imageStream = null;
			imageStreamCreator = null;
			peType = null;
			peInfo = null;
		}

		/// <inheritdoc/>
		public IImageStream CreateStream(FileOffset offset) {
			if ((long)offset > imageStreamCreator.Length)
				throw new ArgumentOutOfRangeException("offset");
			long length = imageStreamCreator.Length - (long)offset;
			return CreateStream(offset, length);
		}

		/// <inheritdoc/>
		public IImageStream CreateStream(FileOffset offset, long length) {
			return imageStreamCreator.Create(offset, length);
		}

		/// <inheritdoc/>
		public IImageStream CreateFullStream() {
			return imageStreamCreator.CreateFull();
		}
	}
}
