﻿using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Reloaded.Mod.Interfaces;
using modloader.Formats.DwPack;
using static modloader.Native;

namespace modloader.Redirectors.DwPack
{
    public unsafe class DwPackRedirector : FileAccessFilter
    {
        private class VirtualDwPack
        {
            public long FilePointer;
            public string FilePath;
            public string FileName;
            public long OriginalFileSize;
            public long MaxFileSize;
            public long CurrentFileSize;
            public DwPackHeader Header;
            public long DataStartOffset;
            public List<VirtualDwPackFile> Files;

            public VirtualDwPack()
            {
                Files = new List<VirtualDwPackFile>();
                MaxFileSize = 0xFFFFFFFF;
            }
        }

        private unsafe class VirtualDwPackFile
        {
            private readonly VirtualDwPack mPack;
            public DwPackFileEntry OriginalEntry;
            public DwPackFileEntry NewEntry;
            public long EntryOffset { get; private set; }
            public bool IsRedirected { get; private set; }
            public string RedirectedPath { get; private set; }
            public long FileSize { get; private set; }

            public VirtualDwPackFile( VirtualDwPack pack, DwPackFileEntry* entry )
            {
                OriginalEntry = *entry;
                NewEntry = *entry;
                mPack = pack;
            }

            public void Redirect( ILogger logger, string newPath )
            {
                RedirectedPath = newPath;
                using ( var stream = OpenRead() )
                    FileSize = stream.Length;

                // Patch file size         
                NewEntry.CompressedSize = ( int )FileSize;
                NewEntry.UncompressedSize = ( int )FileSize;
                NewEntry.Flags = 0;

                var sizeDifference = NewEntry.CompressedSize - OriginalEntry.CompressedSize;
                if ( sizeDifference > 0 )
                {
                    // Patch data offset (hacky)
                    NewEntry.DataOffset = ( int )mPack.CurrentFileSize;
                    mPack.CurrentFileSize += NewEntry.CompressedSize;
                    if ( mPack.CurrentFileSize >= mPack.MaxFileSize )
                        logger.WriteLine( $"[modloader:DwPackRedirector] Out of space!!! 4GB offset space is exhausted", logger.ColorRed );
                }

                IsRedirected = true;
            }

            public Stream OpenRead()
                => new FileStream( RedirectedPath, FileMode.Open, FileAccess.Read, 
                    FileShare.Read | FileShare.Write | FileShare.Delete, 0x300_000, FileOptions.RandomAccess );
        }

        private static readonly Regex sPacFileNameRegex = new Regex(@".+\d{5}.pac", RegexOptions.Compiled | RegexOptions.IgnoreCase);
        private readonly ILogger mLogger;
        private readonly Dictionary<IntPtr, VirtualDwPack> mPacksByHandle;
        private readonly Dictionary<string, VirtualDwPack> mPacksByName;
        private string mLoadDirectory;
        private VirtualDwPackFile mCachedFile;
        private Stream mCachedFileStream;

        public DwPackRedirector( ILogger logger, string loadDirectory )
        {
            mLogger = logger;
            mPacksByHandle = new Dictionary<IntPtr, VirtualDwPack>();
            mPacksByName = new Dictionary<string, VirtualDwPack>( StringComparer.OrdinalIgnoreCase );
            mLoadDirectory = loadDirectory;
        }

        public override bool Accept( string newFilePath )
        {
            if ( sPacFileNameRegex.IsMatch( newFilePath ) )
                return true;

            return false;
        }

        public override bool Accept( IntPtr handle )
        {
            return mPacksByHandle.ContainsKey( handle );
        }

        public override bool CloseHandleImpl( IntPtr handle )
        {
            mPacksByHandle.Remove( handle );
            return mHooks.CloseHandleHook.OriginalFunction( handle );
        }

        public override Native.NtStatus NtCreateFileImpl( string newFilePath, out IntPtr handle, FileAccess access,
            ref Native.OBJECT_ATTRIBUTES objectAttributes, ref Native.IO_STATUS_BLOCK ioStatus, ref long allocSize,
            uint fileAttributes, FileShare share, uint createDisposition, uint createOptions, IntPtr eaBuffer, uint eaLength )
        {
            var status = mHooks.NtCreateFileHook.OriginalFunction( out handle, access, ref objectAttributes, ref ioStatus, ref allocSize,
                fileAttributes, share, createDisposition, createOptions, eaBuffer, eaLength );

#if DEBUG
            if ( mPacksByHandle.ContainsKey( handle ) )
            {
                Debug( $"Hnd {handle} for {newFilePath} is already used by another file!!!" );
            }
            else if ( mPacksByHandle.Any( x => x.Value.FilePath.Equals( newFilePath, StringComparison.OrdinalIgnoreCase ) ) )
            {
                var otherHandle = mPacksByHandle.First( x => x.Value.FilePath.Equals( newFilePath, StringComparison.OrdinalIgnoreCase ) ).Key;
                Debug( $"Hnd {otherHandle} for {newFilePath} already exists!!!" );
            }
#endif

            if ( !mPacksByName.TryGetValue( newFilePath, out var pack ) )
            {
                FILE_STANDARD_INFORMATION fileInfo;
                NtQueryInformationFileImpl( handle, out _, &fileInfo, ( uint )sizeof( FILE_STANDARD_INFORMATION ), FileInformationClass.FileStandardInformation );

                mPacksByName[newFilePath] = pack = new VirtualDwPack()
                {
                    FilePath = newFilePath,
                    FileName = Path.GetFileNameWithoutExtension( newFilePath ),
                    OriginalFileSize = fileInfo.EndOfFile,
                    CurrentFileSize = fileInfo.EndOfFile,
                };

                Debug( $"Registered {newFilePath}" );
            }

            mPacksByHandle[handle] = pack;
            Debug( $"Hnd {handle} {newFilePath} handle registered" );
            return status;
        }

        private NtStatus ReadHeader( IntPtr handle, IntPtr hEvent, IntPtr* apcRoutine, IntPtr* apcContext,
            ref Native.IO_STATUS_BLOCK ioStatus, byte* buffer, uint length, LARGE_INTEGER* byteOffset, IntPtr key,
            VirtualDwPack pack, long offset, long effOffset )
        {
            // Header read
            NtStatus result;
            var header = (DwPackHeader*)buffer;

            // Copy header       
            if ( pack.Header.Signature == 0 )
            {
                // First access
                result = mHooks.NtReadFileHook.OriginalFunction( handle, hEvent, apcRoutine, apcContext, ref ioStatus, buffer, length, byteOffset, key );
                if ( result != NtStatus.Success ) return result;

                pack.Header = *header;
                pack.DataStartOffset = ( long )( sizeof( DwPackHeader ) + ( sizeof( DwPackFileEntry ) * pack.Header.FileCount ) );
                for ( int i = 0; i < pack.Header.FileCount; i++ )
                    pack.Files.Add( null );
                Debug( $"{pack.FileName} Hnd: {handle} DW_PACK header: Index: {pack.Header.Index} FileCount: {pack.Header.FileCount}" );
            }
            else
            {
                // Repeat access
                *header = pack.Header;
                result = NtStatus.Success;
            }

            return result;
        }

        private NtStatus ReadEntry( IntPtr handle, IntPtr hEvent, IntPtr* apcRoutine, IntPtr* apcContext,
            ref Native.IO_STATUS_BLOCK ioStatus, byte* buffer, uint length, LARGE_INTEGER* byteOffset, IntPtr key,
            VirtualDwPack pack, long offset, long effOffset )
        {
            // Entries read
            var entry = (DwPackFileEntry*)buffer;
            var fileIndex = (int)((effOffset - sizeof(DwPackHeader)) / sizeof(DwPackFileEntry));

            if ( fileIndex >= pack.Files.Count )
            {
                Error( $"{pack.FileName} Hnd: {handle} File index out of range!! {fileIndex}" );
            }
            else
            {
                if ( pack.Files[fileIndex] == null )
                {
                    // First access
                    var result = mHooks.NtReadFileHook.OriginalFunction( handle, hEvent, apcRoutine, apcContext, ref ioStatus, buffer, length, byteOffset, key );
                    if ( result != NtStatus.Success ) return result;

                    Debug( $"{pack.FileName} Hnd: {handle} {entry->Path} Entry access Field00: {entry->Field00} Id: {entry->Id} Field104: {entry->Field104}" +
                        $"CompressedSize: {entry->CompressedSize:X8} UncompressedSize: {entry->UncompressedSize:X8} Flags: {entry->Flags} DataOffset: {pack.DataStartOffset + entry->DataOffset:X8}" );

                    var file = pack.Files[fileIndex] = new VirtualDwPackFile( pack, entry );

                    var redirectedFilePath = Path.Combine(mLoadDirectory, pack.FileName, entry->Path);
                    if ( File.Exists( redirectedFilePath ) )
                    {
                        file.Redirect( mLogger, redirectedFilePath );
                        *entry = pack.Files[fileIndex].NewEntry;

                        //Dump( effOffset, length, buffer );
                        Debug( $"{pack.FileName} Hnd: {handle} {entry->Path} Redirected to {redirectedFilePath}" );
                        Debug( $"Patched entry: Field00: {entry->Field00} Id: {entry->Id} Field104: {entry->Field104} " +
                            $"CompressedSize: {entry->CompressedSize:X8} UncompressedSize: {entry->UncompressedSize:X8} " +
                            $"Flags: {entry->Flags} DataOffset: {pack.DataStartOffset + entry->DataOffset:X8}" );
                    }
                    else
                    {
                        Debug( $"No redirection for {entry->Path} because {redirectedFilePath} does not exist." );
                    }
                }
                else
                {
                    Debug( $"{pack.FileName} Hnd: {handle} {entry->Path} Entry access Field00: {entry->Field00} Id: {entry->Id} Field104: {entry->Field104}" +
                        $"CompressedSize: {entry->CompressedSize:X8} UncompressedSize: {entry->UncompressedSize:X8} Flags: {entry->Flags} DataOffset: {pack.DataStartOffset + entry->DataOffset:X8}" );

                    // Repeat access
                    *entry = pack.Files[fileIndex].NewEntry;
                }
            }

            return NtStatus.Success;
        }

        private NtStatus ReadFile( IntPtr handle, IntPtr hEvent, IntPtr* apcRoutine, IntPtr* apcContext,
            ref Native.IO_STATUS_BLOCK ioStatus, byte* buffer, uint length, LARGE_INTEGER* byteOffset, IntPtr key,
            VirtualDwPack pack, long offset, long effOffset )
        {
            // File data read
            NtStatus result = NtStatus.Success;
            var handled = false;
            for ( int i = 0; i < pack.Files.Count; i++ )
            {
                var file = pack.Files[i];
                if ( file == null )
                {
                    // Entry has not been read yet
                    continue;
                }

                var entry = file.NewEntry;
                var dataOffset = pack.DataStartOffset + entry.DataOffset;
                if ( effOffset < dataOffset || effOffset >= ( dataOffset + entry.CompressedSize ) )
                    continue;

                var fileDataOffset = effOffset - dataOffset;
                var readEndOffset = fileDataOffset + length;
                if ( readEndOffset > entry.CompressedSize )
                    continue;

                handled = true;
                if ( !file.IsRedirected )
                {
                    Info( $"{pack.FileName} Hnd: {handle} {entry.Path} Data access Offset: 0x{effOffset:X8} Length: 0x{length:X8}" );
                    result = mHooks.NtReadFileHook.OriginalFunction( handle, hEvent, apcRoutine, apcContext, ref ioStatus, buffer, length, byteOffset, key );
                }
                else
                {
                    Info( $"{pack.FileName} Hnd: {handle} {entry.Path} Data access Offset: 0x{effOffset:X8} Length: 0x{length:X8} redirected to {file.RedirectedPath}" );
                    result = NtStatus.Success;

                    if ( fileDataOffset < 0 )
                    {
                        Error( $"{pack.FileName} Hnd: {handle} {entry.Path} Offset is before start of data!!!" );
                        continue;
                    }
                    else if ( fileDataOffset > file.FileSize )
                    {
                        Error( $"{pack.FileName} Hnd: {handle} {entry.Path} Offset is after end of data!!!" );
                        continue;
                    }
                    else
                    {
                        Debug( $"{pack.FileName} Hnd: {handle} {entry.Path} Reading 0x{length:X8} bytes from redirected file at offset 0x{fileDataOffset:X8}" );
                    }

                    
                    // Get cached file stream if the file was previously opened or open a new file
                    Stream redirectedStream;
                    if ( mCachedFile == file )
                    {
                        redirectedStream = mCachedFileStream;
                    }
                    else
                    {
                        mCachedFileStream?.Close();
                        mCachedFile = file;
                        mCachedFileStream = redirectedStream = file.OpenRead();
                    }

                    // Read from redirected file into the buffer
                    redirectedStream.Seek( fileDataOffset, SeekOrigin.Begin );
                    var readBytes = redirectedStream.Read( new Span<byte>( ( void* )buffer, ( int )length ) );
                    if ( readBytes != length )
                    {
                        Error( $"{pack.FileName} Hnd: {handle} {entry.Path} File read length doesnt match requested read length!! Expected 0x{length:X8}, Actual 0x{readBytes:X8}" );
                    }

                    Debug( $"{pack.FileName} Hnd: {handle} {entry.Path} Wrote redirected file to buffer" );

                    offset += length;
                    NtSetInformationFileImpl( handle, out _, &offset, sizeof( long ), FileInformationClass.FilePositionInformation );

                    // Set number of read bytes.
                    ioStatus.Status = 0;
                    ioStatus.Information = new IntPtr( length );
                }
            }

            if ( !handled )
            {
                Error( $"{pack.FileName} Hnd: {handle} Unhandled file data read request!! Offset: 0x{effOffset:X8} Length: 0x{length:X8}" );
                result = mHooks.NtReadFileHook.OriginalFunction( handle, hEvent, apcRoutine, apcContext, ref ioStatus, buffer, length, byteOffset, key );
            }

            return result;
        }

        public override unsafe Native.NtStatus NtReadFileImpl( IntPtr handle, IntPtr hEvent, IntPtr* apcRoutine, IntPtr* apcContext,
            ref Native.IO_STATUS_BLOCK ioStatus, byte* buffer, uint length, LARGE_INTEGER* byteOffset, IntPtr key )
        {
            NtStatus result;
            var pack = mPacksByHandle[handle];
            var offset = pack.FilePointer;
            var reqOffset = ( byteOffset != null || ( byteOffset != null && byteOffset->HighPart == -1 && byteOffset->LowPart == FILE_USE_FILE_POINTER_POSITION )) ?
                byteOffset->QuadPart : -1;
            var effOffset = reqOffset == -1 ? offset : reqOffset;

            if ( effOffset == 0 && length == sizeof( DwPackHeader ) )
            {
                result = ReadHeader( handle, hEvent, apcRoutine, apcContext, ref ioStatus, buffer, length, byteOffset, key, pack, offset, effOffset );
            }
            else if ( ( effOffset >= sizeof( DwPackHeader ) && effOffset < pack.DataStartOffset ) && length == sizeof( DwPackFileEntry ) )
            {
                result = ReadEntry( handle, hEvent, apcRoutine, apcContext, ref ioStatus, buffer, length, byteOffset, key, pack, offset, effOffset );
            }
            else if ( effOffset >= pack.DataStartOffset )
            {
                result = ReadFile( handle, hEvent, apcRoutine, apcContext, ref ioStatus, buffer, length, byteOffset, key, pack, offset, effOffset );
            }
            else
            {
                Error( $"{pack.FileName} Hnd: {handle} Unexpected read request!! Offset: {effOffset:X8} Length: {length:X8}" );
                result = mHooks.NtReadFileHook.OriginalFunction( handle, hEvent, apcRoutine, apcContext, ref ioStatus, buffer, length, byteOffset, key );
            }

            if ( result != NtStatus.Success )
                Error( $"{pack.FileName} Hnd: {handle} NtReadFile failed with {result}!!!" );

            return result;
        }

        public override unsafe NtStatus NtSetInformationFileImpl( IntPtr hfile, out IO_STATUS_BLOCK ioStatusBlock,
            void* fileInformation, uint length, FileInformationClass fileInformationClass )
        {

            if ( fileInformationClass == FileInformationClass.FilePositionInformation )
            {
                var pack = mPacksByHandle[hfile];
                pack.FilePointer = *( long* )fileInformation;
                Debug( $"{pack.FileName} Hnd: {hfile} SetFilePointer -> 0x{pack.FilePointer:X8}" );
            }
            else
            {
                Warning( $"SetInformationFileImpl(hfile = {hfile}, out ioStatusBlock, fileInformation = *0x{( long )fileInformation:X8}, " +
                    $"length = {length}, fileInformationClass = {fileInformationClass}" );
            }

            mHooks.NtSetInformationFileHook.OriginalFunction( hfile, out ioStatusBlock, fileInformation, length, fileInformationClass );

            // Spoof return value as we extend beyond the end of the file
            return NtStatus.Success;
        }


        public override unsafe NtStatus NtQueryInformationFileImpl( IntPtr hfile, out Native.IO_STATUS_BLOCK ioStatusBlock, void* fileInformation, uint length, Native.FileInformationClass fileInformationClass )
        {
            var result = mHooks.NtQueryInformationFileHook.OriginalFunction( hfile, out ioStatusBlock, fileInformation, length, fileInformationClass );
            if ( !mPacksByHandle.TryGetValue( hfile, out var pack ) )
                return result;

            if ( fileInformationClass == FileInformationClass.FileStandardInformation )
            {
                var info = (FILE_STANDARD_INFORMATION*)fileInformation;
                info->EndOfFile = pack.MaxFileSize;
            }
            else
            {
                Debug( $"NtQueryInformationFileImpl( IntPtr hfile = {hfile}, out Native.IO_STATUS_BLOCK ioStatusBlock, void* fileInformation, length = {length}, fileInformationClass = {fileInformationClass} )" );
            }

            return result;
        }

        private void Dump( long offset, uint length, byte* buffer )
        {
            mHooks.Disable();
            using ( var dump = new FileStream( $"{DateTime.Now}-dump_{offset:X8}_{length:X8}.bin", FileMode.Create ) )
                dump.Write( new ReadOnlySpan<byte>( ( void* )buffer, ( int )length ) );
            mHooks.Enable();
        }

        private void Info( string msg ) => mLogger?.WriteLine( $"[modloader:DwPackRedirector] I {msg}" );
        private void Warning( string msg ) => mLogger?.WriteLine( $"[modloader:DwPackRedirector] W {msg}", mLogger.ColorYellow );
        private void Error( string msg ) => mLogger?.WriteLine( $"[modloader:DwPackRedirector] E {msg}", mLogger.ColorRed );

        [Conditional( "DEBUG" )]
        private void Debug( string msg ) => mLogger?.WriteLine( $"[modloader:DwPackRedirector] D {msg}", mLogger.ColorGreen );
    }
}