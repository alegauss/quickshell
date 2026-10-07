using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;
using System.Text;
using System.Windows.Threading;
using Quickshell.Transport;

namespace Quickshell.App;

/// <summary>
/// Files dragged out of the host's pane onto Explorer or the desktop — a download the drop asks for
/// (QS188).
///
/// <para><b>Windows asks for a dragged file's bytes during the drop, and a server over a slow link
/// cannot answer in that time.</b> So nothing is downloaded when the drag starts. What is offered is
/// the shell's virtual-file pair: <c>FileGroupDescriptorW</c>, the names, sizes and times the
/// listing already has, and <c>FileContents</c>, one stream per file read from the session's file
/// channel only as Explorer reads it.</para>
///
/// <para><b>Explorer copies on its own thread, with its own progress, and the drop returns at
/// once.</b> That is the shell's asynchronous data object: offered here, Explorer starts an
/// operation, lets the drop finish and reads the streams in the background — which is what a large
/// file over a slow link needs and what makes a placeholder and a queued download unnecessary.
/// </para>
///
/// <para><b>Served from a thread of its own.</b> Explorer's reads arrive through COM, on whichever
/// thread the object lives on; living on the window's, every block read from the server would hold
/// the window. The object is made and pumped on its own thread, and the window drags a proxy to it.
/// </para>
/// </summary>
public static partial class RemoteDrag
{
    /// <summary>The shell's name for the list of virtual files.</summary>
    public const string Descriptor = "FileGroupDescriptorW";

    /// <summary>The shell's name for one virtual file's bytes.</summary>
    public const string Contents = "FileContents";

    /// <summary>DROPEFFECT_COPY: a drag out of a server copies; it never moves anything off it.</summary>
    public const uint Copy = 1;

    private static readonly StrategyBasedComWrappers Wrappers = new();

    /// <summary>
    /// Drags files of the host's out, returning once the drop is over — the copy itself may still be
    /// running in Explorer, which owns it from there.
    /// </summary>
    /// <param name="channel">The session's file channel, which this does not own.</param>
    /// <param name="files">The files, with the paths they have on the server.</param>
    /// <returns>Whether they were dropped somewhere that took them.</returns>
    public static bool Drag(IFileTransferChannel channel, IReadOnlyList<(string Path, FileItem Item)> files)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(files);

        if (files.Count == 0)
        {
            return false;
        }

        using Served served = Serve(channel, files);

        nint source = Pointer(new DropSource(), typeof(IDropSource).GUID);

        try
        {
            int result = DoDragDrop(served.Proxy, source, Copy, out uint effect);

            served.Dropped(result == DropDone && effect != 0);

            return result == DropDone && effect != 0;
        }
        finally
        {
            Marshal.Release(source);
        }
    }

    /// <summary>
    /// The data object on a thread of its own, and a proxy to it for this one. Disposed once the drop
    /// is over, it keeps its thread until Explorer has finished reading, and no longer.
    /// </summary>
    public static Served Serve(IFileTransferChannel channel, IReadOnlyList<(string Path, FileItem Item)> files)
    {
        ArgumentNullException.ThrowIfNull(channel);
        ArgumentNullException.ThrowIfNull(files);

        Guid iid = typeof(IDataObject).GUID;
        nint marshalled = 0;
        Dispatcher? pump = null;
        VirtualFiles? data = null;
        Exception? failed = null;

        using ManualResetEventSlim ready = new();

        Thread thread = new(() =>
        {
            try
            {
                data = new VirtualFiles(channel, files);

                nint own = Pointer(data, iid);

                try
                {
                    Marshal.ThrowExceptionForHR(CoMarshalInterThreadInterfaceInStream(iid, own, out marshalled));
                }
                finally
                {
                    Marshal.Release(own);
                }

                pump = Dispatcher.CurrentDispatcher;
            }
            catch (Exception error)
            {
                failed = error;
            }
            finally
            {
                ready.Set();
            }

            if (failed is null)
            {
                Dispatcher.Run();
            }
        })
        {
            IsBackground = true,
            Name = "Remote drag",
        };

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        if (failed is not null)
        {
            throw new InvalidOperationException("the dragged files could not be offered", failed);
        }

        Marshal.ThrowExceptionForHR(CoGetInterfaceAndReleaseStream(marshalled, iid, out nint proxy));

        return new Served(proxy, data!, pump!);
    }

    /// <summary>
    /// Takes virtual files out of a data object into a folder, the way Explorer does with a drop:
    /// the descriptor for the names, then each file's stream, inside the object's asynchronous
    /// operation where it offers one. What a second browser will do with a drag from the first, and
    /// how a test drops without a desk.
    /// </summary>
    /// <param name="data">An <c>IDataObject*</c>, which stays the caller's.</param>
    /// <param name="folder">Where the files land.</param>
    /// <returns>The files written.</returns>
    public static unsafe IReadOnlyList<string> Extract(nint data, string folder)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);

        IDataObject dropped = Object<IDataObject>(data);
        IDataObjectAsyncCapability? operation = null;

        if (Marshal.QueryInterface(data, typeof(IDataObjectAsyncCapability).GUID, out nint capability) == 0)
        {
            operation = Object<IDataObjectAsyncCapability>(capability);
            Marshal.Release(capability);
        }

        int asynchronous = 0;

        if (operation is not null)
        {
            operation.GetAsyncMode(&asynchronous);
        }

        if (asynchronous != 0)
        {
            operation!.StartOperation(0);
        }

        List<string> written = [];
        int result = 0;

        try
        {
            FormatEtc describe = new() { CfFormat = Format(Descriptor), Aspect = 1, Lindex = -1, Tymed = 1 };
            StgMedium medium;

            Marshal.ThrowExceptionForHR(dropped.GetData(&describe, &medium));

            List<string> names = [];

            try
            {
                byte* group = (byte*)VirtualFiles.GlobalLock(medium.Handle);
                uint count = *(uint*)group;

                for (int index = 0; index < count; index++)
                {
                    names.Add(new string((char*)(group + 4 + (index * 592) + 72)));
                }

                VirtualFiles.GlobalUnlock(medium.Handle);
            }
            finally
            {
                VirtualFiles.ReleaseStgMedium(&medium);
            }

            byte[] block = new byte[64 * 1024];

            for (int index = 0; index < names.Count; index++)
            {
                FormatEtc contents = new() { CfFormat = Format(Contents), Aspect = 1, Lindex = index, Tymed = 4 };
                StgMedium streamed;

                Marshal.ThrowExceptionForHR(dropped.GetData(&contents, &streamed));

                string path = System.IO.Path.Combine(folder, names[index]);

                try
                {
                    IStream stream = Object<IStream>(streamed.Handle);

                    using FileStream into = new(path, FileMode.Create, FileAccess.Write);

                    fixed (byte* buffer = block)
                    {
                        while (true)
                        {
                            uint read;
                            int hr = stream.Read(buffer, (uint)block.Length, &read);

                            Marshal.ThrowExceptionForHR(hr);
                            into.Write(block, 0, (int)read);

                            if (hr != 0 || read == 0)
                            {
                                break;
                            }
                        }
                    }
                }
                finally
                {
                    VirtualFiles.ReleaseStgMedium(&streamed);
                }

                written.Add(path);
            }
        }
        catch (Exception failed)
        {
            result = failed.HResult;

            throw;
        }
        finally
        {
            if (asynchronous != 0)
            {
                operation!.EndOperation(result, 0, result == 0 ? Copy : 0);
            }
        }

        return written;
    }

    /// <summary>A pointer to a managed object's implementation of one COM interface, owned by the caller.</summary>
    internal static nint Pointer(object instance, Guid iid)
    {
        nint unknown = Wrappers.GetOrCreateComInterfaceForObject(instance, CreateComInterfaceFlags.None);

        try
        {
            Marshal.ThrowExceptionForHR(Marshal.QueryInterface(unknown, iid, out nint wanted));

            return wanted;
        }
        finally
        {
            Marshal.Release(unknown);
        }
    }

    /// <summary>A COM object as one of this file's interfaces, for whoever reads a pointer back.</summary>
    internal static T Object<T>(nint pointer) => (T)Wrappers.GetOrCreateObjectForComInstance(pointer, CreateObjectFlags.UniqueInstance);

    /// <summary>The number Windows gives a clipboard format's name, the same in every process.</summary>
    internal static ushort Format(string name) => (ushort)RegisterClipboardFormatW(name);

    /// <summary>DRAGDROP_S_DROP: the drag ended in a drop.</summary>
    private const int DropDone = 0x40100;

    [LibraryImport("ole32.dll")]
    private static partial int DoDragDrop(nint data, nint source, uint allowed, out uint effect);

    [LibraryImport("ole32.dll")]
    private static partial int CoMarshalInterThreadInterfaceInStream(in Guid iid, nint unknown, out nint stream);

    [LibraryImport("ole32.dll")]
    private static partial int CoGetInterfaceAndReleaseStream(nint stream, in Guid iid, out nint instance);

    [LibraryImport("user32.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial uint RegisterClipboardFormatW(string name);

    /// <summary>
    /// A proxy to the data object, and the thread serving it — kept until Explorer has read what it
    /// is going to read.
    /// </summary>
    public sealed class Served : IDisposable
    {
        private readonly VirtualFiles _data;
        private readonly Dispatcher _pump;
        private bool _dropped;

        internal Served(nint proxy, VirtualFiles data, Dispatcher pump)
        {
            Proxy = proxy;
            _data = data;
            _pump = pump;
        }

        /// <summary>The data object, as this thread may call it: an <c>IDataObject*</c>, owned by this.</summary>
        public nint Proxy { get; private set; }

        /// <summary>Whether whoever took the drop started the object's background operation.</summary>
        public bool Operated => _data.Started;

        /// <summary>Says whether the drag ended in a drop, which decides how long the thread is kept.</summary>
        internal void Dropped(bool dropped) => _dropped = dropped;

        /// <summary>
        /// Lets go of the proxy, and of the thread once nobody is reading: at once where nothing was
        /// dropped or the copy already happened inside the drop, and when Explorer ends its operation
        /// where it copies in the background.
        /// </summary>
        public void Dispose()
        {
            if (Proxy != 0)
            {
                Marshal.Release(Proxy);
                Proxy = 0;
            }

            if (_dropped && _data.Started)
            {
                _data.Finished += () => _pump.InvokeShutdown();

                if (!_data.Running)
                {
                    _pump.InvokeShutdown();
                }

                return;
            }

            _pump.InvokeShutdown();
        }
    }
}

/// <summary>
/// The virtual files themselves: their descriptor, a stream each, and the shell's asynchronous
/// operation (QS188).
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class VirtualFiles : IDataObject, IDataObjectAsyncCapability
{
    private const int Ok = 0;
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int BadFormat = unchecked((int)0x80040064);
    private const int BadIndex = unchecked((int)0x80040065);
    private const int NoAdvise = unchecked((int)0x80040003);
    private const uint HGlobal = 1;
    private const uint IStreamMedium = 4;

    private readonly IFileTransferChannel _channel;
    private readonly IReadOnlyList<(string Path, FileItem Item)> _files;
    private readonly ushort _descriptor = RemoteDrag.Format(RemoteDrag.Descriptor);
    private readonly ushort _contents = RemoteDrag.Format(RemoteDrag.Contents);
    private readonly ushort _effect = RemoteDrag.Format("Preferred DropEffect");
    private readonly Dictionary<ushort, byte[]> _set = [];
    private readonly object _guard = new();

    private bool _async = true;

    public VirtualFiles(IFileTransferChannel channel, IReadOnlyList<(string Path, FileItem Item)> files)
    {
        _channel = channel;
        _files = files;
    }

    /// <summary>Whether Explorer started an operation, which means it copies after the drop returns.</summary>
    public bool Started { get; private set; }

    /// <summary>Whether that operation is still going.</summary>
    public bool Running { get; private set; }

    /// <summary>Raised when Explorer ends the operation it started.</summary>
    public event Action? Finished;

    /// <summary>The descriptor's bytes: a count, then one 592-byte FILEDESCRIPTORW per file.</summary>
    public byte[] DescriptorBytes()
    {
        const int Each = 592;

        byte[] bytes = new byte[4 + (Each * _files.Count)];

        BitConverter.TryWriteBytes(bytes.AsSpan(0, 4), (uint)_files.Count);

        for (int index = 0; index < _files.Count; index++)
        {
            FileItem item = _files[index].Item;
            Span<byte> one = bytes.AsSpan(4 + (index * Each), Each);

            // FD_ATTRIBUTES | FD_WRITESTIME | FD_FILESIZE | FD_PROGRESSUI | FD_UNICODE.
            BitConverter.TryWriteBytes(one[0..], 0x4u | 0x20u | 0x40u | 0x4000u | 0x80000000u);

            // FILE_ATTRIBUTE_NORMAL: a file, which is all a drag out of a server offers.
            BitConverter.TryWriteBytes(one[36..], 0x80u);
            BitConverter.TryWriteBytes(one[56..], item.Modified.ToFileTime());
            BitConverter.TryWriteBytes(one[64..], (uint)((ulong)item.Length >> 32));
            BitConverter.TryWriteBytes(one[68..], (uint)((ulong)item.Length & 0xFFFFFFFF));

            string name = item.Name.Length > 259 ? item.Name[..259] : item.Name;

            Encoding.Unicode.GetBytes(name, one[72..]);
        }

        return bytes;
    }

    public int GetData(FormatEtc* format, StgMedium* medium)
    {
        *medium = default;

        if (format->CfFormat == _contents)
        {
            if ((format->Tymed & IStreamMedium) == 0)
            {
                return BadFormat;
            }

            if (format->Lindex < 0 || format->Lindex >= _files.Count)
            {
                return BadIndex;
            }

            (string path, FileItem item) = _files[format->Lindex];

            medium->Tymed = IStreamMedium;
            medium->Handle = RemoteDrag.Pointer(new ChannelStream(_channel, path, item), typeof(IStream).GUID);

            return Ok;
        }

        if ((format->Tymed & HGlobal) == 0)
        {
            return BadFormat;
        }

        byte[]? bytes;

        if (format->CfFormat == _descriptor)
        {
            bytes = DescriptorBytes();
        }
        else if (format->CfFormat == _effect)
        {
            bytes = BitConverter.GetBytes(RemoteDrag.Copy);
        }
        else
        {
            lock (_guard)
            {
                _set.TryGetValue(format->CfFormat, out bytes);
            }
        }

        if (bytes is null)
        {
            return BadFormat;
        }

        medium->Tymed = HGlobal;
        medium->Handle = Global(bytes);

        return Ok;
    }

    public int GetDataHere(FormatEtc* format, StgMedium* medium) => NotImplemented;

    public int QueryGetData(FormatEtc* format)
    {
        if (format->CfFormat == _contents)
        {
            return (format->Tymed & IStreamMedium) != 0 ? Ok : BadFormat;
        }

        if ((format->Tymed & HGlobal) == 0)
        {
            return BadFormat;
        }

        if (format->CfFormat == _descriptor || format->CfFormat == _effect)
        {
            return Ok;
        }

        lock (_guard)
        {
            return _set.ContainsKey(format->CfFormat) ? Ok : BadFormat;
        }
    }

    public int GetCanonicalFormatEtc(FormatEtc* format, FormatEtc* canonical)
    {
        *canonical = *format;
        canonical->Ptd = 0;

        return 0x00040130; // DATA_S_SAMEFORMATETC
    }

    /// <summary>
    /// Keeps what the shell sets on a dragged object — the drop description, whether the drop was a
    /// paste — so it can be read back, which the shell does. Only memory is kept; anything else is
    /// declined and stays the caller's.
    /// </summary>
    public int SetData(FormatEtc* format, StgMedium* medium, int release)
    {
        if (medium->Tymed != HGlobal || medium->Handle == 0)
        {
            return NotImplemented;
        }

        nint size = GlobalSize(medium->Handle);
        nint locked = GlobalLock(medium->Handle);

        if (locked == 0)
        {
            return NotImplemented;
        }

        byte[] bytes = new byte[size];

        try
        {
            Marshal.Copy(locked, bytes, 0, bytes.Length);
        }
        finally
        {
            GlobalUnlock(medium->Handle);
        }

        lock (_guard)
        {
            _set[format->CfFormat] = bytes;
        }

        if (release != 0)
        {
            ReleaseStgMedium(medium);
        }

        return Ok;
    }

    public int EnumFormatEtc(uint direction, nint* enumerator)
    {
        *enumerator = 0;

        // DATADIR_GET: what can be read. Nothing is offered to be set by enumeration.
        if (direction != 1)
        {
            return NotImplemented;
        }

        FormatEtc* formats = stackalloc FormatEtc[3];

        formats[0] = new FormatEtc { CfFormat = _descriptor, Aspect = 1, Lindex = -1, Tymed = HGlobal };
        formats[1] = new FormatEtc { CfFormat = _contents, Aspect = 1, Lindex = -1, Tymed = IStreamMedium };
        formats[2] = new FormatEtc { CfFormat = _effect, Aspect = 1, Lindex = -1, Tymed = HGlobal };

        return SHCreateStdEnumFmtEtc(3, formats, enumerator);
    }

    public int DAdvise(FormatEtc* format, uint flags, nint sink, uint* connection) => NoAdvise;

    public int DUnadvise(uint connection) => NoAdvise;

    public int EnumDAdvise(nint* enumerator) => NoAdvise;

    public int SetAsyncMode(int asynchronous)
    {
        _async = asynchronous != 0;

        return Ok;
    }

    public int GetAsyncMode(int* asynchronous)
    {
        *asynchronous = _async ? 1 : 0;

        return Ok;
    }

    public int StartOperation(nint context)
    {
        Started = true;
        Running = true;

        return Ok;
    }

    public int InOperation(int* running)
    {
        *running = Running ? 1 : 0;

        return Ok;
    }

    public int EndOperation(int result, nint context, uint effects)
    {
        Running = false;
        Finished?.Invoke();

        return Ok;
    }

    /// <summary>Bytes in movable memory, which is what a clipboard format's HGLOBAL is.</summary>
    private static nint Global(byte[] bytes)
    {
        nint memory = GlobalAlloc(0x0002, (nuint)bytes.Length);

        if (memory == 0)
        {
            throw new InvalidOperationException("no memory for a dragged format");
        }

        Marshal.Copy(bytes, 0, GlobalLock(memory), bytes.Length);
        GlobalUnlock(memory);

        return memory;
    }

    [LibraryImport("kernel32.dll")]
    private static partial nint GlobalAlloc(uint flags, nuint bytes);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GlobalLock(nint memory);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    internal static partial bool GlobalUnlock(nint memory);

    [LibraryImport("kernel32.dll")]
    internal static partial nint GlobalSize(nint memory);

    [LibraryImport("ole32.dll")]
    internal static partial void ReleaseStgMedium(StgMedium* medium);

    [LibraryImport("shell32.dll")]
    private static partial int SHCreateStdEnumFmtEtc(uint count, FormatEtc* formats, nint* enumerator);
}

/// <summary>
/// One remote file's bytes as a COM stream, opened on the server at the first read and read as
/// Explorer asks — on the thread serving the drag, never the window's (QS188).
/// </summary>
[GeneratedComClass]
internal sealed unsafe partial class ChannelStream(IFileTransferChannel channel, string path, FileItem item) : IStream
{
    private const int Ok = 0;
    private const int NotImplemented = unchecked((int)0x80004001);
    private const int ReadFault = unchecked((int)0x8003001E); // STG_E_READFAULT

    private Stream? _open;
    private long _at;

    public int Read(byte* buffer, uint count, uint* read)
    {
        try
        {
            _open ??= Opened();

            int total = 0;
            Span<byte> into = new(buffer, (int)count);

            // Filled where the server has it: a short read from a stream means its end to most readers.
            while (total < into.Length)
            {
                int got = _open.Read(into[total..]);

                if (got == 0)
                {
                    break;
                }

                total += got;
            }

            _at += total;

            if (read is not null)
            {
                *read = (uint)total;
            }

            return total < count ? 1 : Ok; // S_FALSE at the end, as a stream says it
        }
        catch (Exception)
        {
            if (read is not null)
            {
                *read = 0;
            }

            return ReadFault;
        }
    }

    public int Write(byte* buffer, uint count, uint* written) => NotImplemented;

    public int Seek(long move, uint origin, ulong* position)
    {
        long to = origin switch
        {
            0 => move,
            1 => _at + move,
            2 => item.Length + move,
            _ => -1,
        };

        if (to < 0)
        {
            return unchecked((int)0x80030019); // STG_E_INVALIDFUNCTION
        }

        if (to != _at)
        {
            try
            {
                _open ??= Opened();
                _open.Seek(to, SeekOrigin.Begin);
            }
            catch (Exception)
            {
                return ReadFault;
            }

            _at = to;
        }

        if (position is not null)
        {
            *position = (ulong)_at;
        }

        return Ok;
    }

    public int SetSize(ulong size) => NotImplemented;

    public int CopyTo(nint stream, ulong count, ulong* read, ulong* written) => NotImplemented;

    public int Commit(uint flags) => Ok;

    public int Revert() => NotImplemented;

    public int LockRegion(ulong offset, ulong count, uint type) => NotImplemented;

    public int UnlockRegion(ulong offset, ulong count, uint type) => NotImplemented;

    public int Stat(StatStg* stat, uint flags)
    {
        *stat = default;
        stat->Type = 2; // STGTY_STREAM
        stat->Size = (ulong)item.Length;
        stat->Modified = item.Modified.ToFileTime();

        return Ok;
    }

    public int Clone(nint* clone)
    {
        *clone = 0;

        return NotImplemented;
    }

    private Stream Opened() => channel.OpenReadAsync(path).AsTask().GetAwaiter().GetResult();
}

/// <summary>Ends the drag in a drop when the button comes up, and abandons it on Escape.</summary>
[GeneratedComClass]
internal sealed partial class DropSource : IDropSource
{
    public int QueryContinueDrag(int escape, uint keys)
    {
        if (escape != 0)
        {
            return 0x40101; // DRAGDROP_S_CANCEL
        }

        // MK_LBUTTON | MK_RBUTTON: whichever started it, it is over when neither is down.
        return (keys & 0x3) == 0 ? 0x40100 : 0;
    }

    public int GiveFeedback(uint effect) => 0x40102; // DRAGDROP_S_USEDEFAULTCURSORS
}

/// <summary>FORMATETC.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct FormatEtc
{
    public ushort CfFormat;
    public nint Ptd;
    public uint Aspect;
    public int Lindex;
    public uint Tymed;
}

/// <summary>STGMEDIUM, its union being the one handle every medium here is.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StgMedium
{
    public uint Tymed;
    public nint Handle;
    public nint ReleaseWith;
}

/// <summary>STATSTG, with its three FILETIMEs as the 64-bit numbers they are.</summary>
[StructLayout(LayoutKind.Sequential)]
internal struct StatStg
{
    public nint Name;
    public uint Type;
    public ulong Size;
    public long Modified;
    public long Created;
    public long Accessed;
    public uint Mode;
    public uint LocksSupported;
    public Guid Class;
    public uint StateBits;
    public uint Reserved;
}

/// <summary>IDataObject, every method in vtable order.</summary>
[GeneratedComInterface]
[Guid("0000010E-0000-0000-C000-000000000046")]
internal unsafe partial interface IDataObject
{
    [PreserveSig]
    int GetData(FormatEtc* format, StgMedium* medium);

    [PreserveSig]
    int GetDataHere(FormatEtc* format, StgMedium* medium);

    [PreserveSig]
    int QueryGetData(FormatEtc* format);

    [PreserveSig]
    int GetCanonicalFormatEtc(FormatEtc* format, FormatEtc* canonical);

    [PreserveSig]
    int SetData(FormatEtc* format, StgMedium* medium, int release);

    [PreserveSig]
    int EnumFormatEtc(uint direction, nint* enumerator);

    [PreserveSig]
    int DAdvise(FormatEtc* format, uint flags, nint sink, uint* connection);

    [PreserveSig]
    int DUnadvise(uint connection);

    [PreserveSig]
    int EnumDAdvise(nint* enumerator);
}

/// <summary>IDataObjectAsyncCapability, which the shell calls IAsyncOperation where it is older.</summary>
[GeneratedComInterface]
[Guid("3D8B0590-F691-11D2-8EA9-006097DF5BD4")]
internal unsafe partial interface IDataObjectAsyncCapability
{
    [PreserveSig]
    int SetAsyncMode(int asynchronous);

    [PreserveSig]
    int GetAsyncMode(int* asynchronous);

    [PreserveSig]
    int StartOperation(nint context);

    [PreserveSig]
    int InOperation(int* running);

    [PreserveSig]
    int EndOperation(int result, nint context, uint effects);
}

/// <summary>IStream, with ISequentialStream's two methods first where the vtable has them.</summary>
[GeneratedComInterface]
[Guid("0000000C-0000-0000-C000-000000000046")]
internal unsafe partial interface IStream
{
    [PreserveSig]
    int Read(byte* buffer, uint count, uint* read);

    [PreserveSig]
    int Write(byte* buffer, uint count, uint* written);

    [PreserveSig]
    int Seek(long move, uint origin, ulong* position);

    [PreserveSig]
    int SetSize(ulong size);

    [PreserveSig]
    int CopyTo(nint stream, ulong count, ulong* read, ulong* written);

    [PreserveSig]
    int Commit(uint flags);

    [PreserveSig]
    int Revert();

    [PreserveSig]
    int LockRegion(ulong offset, ulong count, uint type);

    [PreserveSig]
    int UnlockRegion(ulong offset, ulong count, uint type);

    [PreserveSig]
    int Stat(StatStg* stat, uint flags);

    [PreserveSig]
    int Clone(nint* clone);
}

/// <summary>IDropSource.</summary>
[GeneratedComInterface]
[Guid("00000121-0000-0000-C000-000000000046")]
internal partial interface IDropSource
{
    [PreserveSig]
    int QueryContinueDrag(int escape, uint keys);

    [PreserveSig]
    int GiveFeedback(uint effect);
}
