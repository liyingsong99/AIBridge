using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using Vortice.Direct3D;
using Vortice.Direct3D11;
using Vortice.DXGI;

namespace AIBridgeEditorCapture
{
    // WGC COM vtable adapter derived from WgcSharp (MIT), reduced to WGC-only capture.
    internal static class WgcCapture
    {
        [DllImport("d3d11.dll", EntryPoint = "CreateDirect3D11DeviceFromDXGIDevice", PreserveSig = false)]
        private static extern void CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int RoInitialize(int initializationType);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int RoGetActivationFactory(IntPtr classId, [In] ref Guid iid, out IntPtr factory);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int WindowsCreateString(
            [MarshalAs(UnmanagedType.LPWStr)] string sourceString,
            int length,
            out IntPtr hstring);

        [DllImport("combase.dll", PreserveSig = true)]
        private static extern int WindowsDeleteString(IntPtr hstring);

        [ComImport]
        [Guid("3628E81B-3CAC-4C60-B7F4-23CE0E0C3356")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IGraphicsCaptureItemInterop
        {
            IntPtr CreateForWindow([In] IntPtr window, [In] ref Guid iid, out IntPtr result);
        }

        [ComImport]
        [Guid("A9B3D012-3DF2-4EE3-B8D1-8695F457D3C1")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDirect3DDxgiInterfaceAccess
        {
            IntPtr GetInterface([In] ref Guid iid);
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SizeInt32
        {
            public int Width;
            public int Height;
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetSizeDelegate(IntPtr thisPointer, out SizeInt32 size);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateFreeThreadedDelegate(
            IntPtr thisPointer,
            IntPtr device,
            int pixelFormat,
            int numberOfBuffers,
            SizeInt32 size,
            out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int TryGetNextFrameDelegate(IntPtr thisPointer, out IntPtr frame);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CreateCaptureSessionDelegate(IntPtr thisPointer, IntPtr item, out IntPtr session);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int StartCaptureDelegate(IntPtr thisPointer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int PutBooleanDelegate(IntPtr thisPointer, byte value);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetSurfaceDelegate(IntPtr thisPointer, out IntPtr surface);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int QueryInterfaceDelegate(IntPtr thisPointer, ref Guid iid, out IntPtr result);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate uint ReleaseDelegate(IntPtr thisPointer);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int CloseDelegate(IntPtr thisPointer);

        private static readonly Guid GraphicsCaptureItemId = new Guid("79C3F95B-31F7-4EC2-A464-632EF5D30760");
        private static readonly Guid FramePoolStatics2Id = new Guid("589B103F-6BBC-5DF5-A991-02E28B3B66D5");
        private static readonly Guid GraphicsCaptureSession3Id = new Guid("F2CDD966-22AE-5EA1-9596-3A289344C3BE");
        private static readonly Guid ClosableId = new Guid("30D5A829-7FA4-4026-83BB-D75BAE4EA99E");

        public static Bitmap Capture(IntPtr window, int timeoutMilliseconds)
        {
            var initializeResult = RoInitialize(1);
            if (initializeResult < 0 && (uint)initializeResult != 0x80010106u)
            {
                Marshal.ThrowExceptionForHR(initializeResult);
            }

            D3D11.D3D11CreateDevice(
                IntPtr.Zero,
                DriverType.Hardware,
                DeviceCreationFlags.BgraSupport,
                null,
                out var d3dDevice,
                out var d3dContext).CheckError();

            using (d3dDevice)
            using (d3dContext)
            using (var dxgiDevice = d3dDevice.QueryInterface<IDXGIDevice>())
            {
                IntPtr direct3DDevice;
                CreateDirect3D11DeviceFromDXGIDevice(dxgiDevice.NativePointer, out direct3DDevice);

                var item = IntPtr.Zero;
                var framePool = IntPtr.Zero;
                var session = IntPtr.Zero;
                try
                {
                    item = CreateCaptureItem(window);
                    var getSize = GetVtableMethod<GetSizeDelegate>(item, 7);
                    SizeInt32 size;
                    Marshal.ThrowExceptionForHR(getSize(item, out size));
                    if (size.Width <= 0 || size.Height <= 0)
                    {
                        return null;
                    }

                    framePool = CreateFramePool(direct3DDevice, size);
                    var createSession = GetVtableMethod<CreateCaptureSessionDelegate>(framePool, 10);
                    Marshal.ThrowExceptionForHR(createSession(framePool, item, out session));
                    TryDisableCaptureBorder(session);

                    var startCapture = GetVtableMethod<StartCaptureDelegate>(session, 6);
                    Marshal.ThrowExceptionForHR(startCapture(session));

                    var tryGetNextFrame = GetVtableMethod<TryGetNextFrameDelegate>(framePool, 7);
                    var stopwatch = System.Diagnostics.Stopwatch.StartNew();
                    var frameNumber = 0;
                    while (stopwatch.ElapsedMilliseconds < timeoutMilliseconds)
                    {
                        IntPtr frame;
                        var result = tryGetNextFrame(framePool, out frame);
                        if (result < 0 || frame == IntPtr.Zero)
                        {
                            Thread.Sleep(16);
                            continue;
                        }

                        frameNumber++;
                        if (frameNumber == 1)
                        {
                            CloseAndRelease(frame);
                            Thread.Sleep(50);
                            continue;
                        }

                        try
                        {
                            var getSurface = GetVtableMethod<GetSurfaceDelegate>(frame, 6);
                            IntPtr surface;
                            Marshal.ThrowExceptionForHR(getSurface(frame, out surface));
                            try
                            {
                                return SurfaceToBitmap(surface, d3dDevice, d3dContext);
                            }
                            finally
                            {
                                CloseAndRelease(surface);
                            }
                        }
                        finally
                        {
                            CloseAndRelease(frame);
                        }
                    }

                    return null;
                }
                finally
                {
                    CloseAndRelease(session);
                    CloseAndRelease(framePool);
                    Release(item);
                    CloseAndRelease(direct3DDevice);
                }
            }
        }

        private static IntPtr CreateCaptureItem(IntPtr window)
        {
            const string className = "Windows.Graphics.Capture.GraphicsCaptureItem";
            IntPtr classId;
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out classId));
            try
            {
                var interopId = typeof(IGraphicsCaptureItemInterop).GUID;
                IntPtr factoryPointer;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref interopId, out factoryPointer));
                try
                {
                    var interop = (IGraphicsCaptureItemInterop)Marshal.GetObjectForIUnknown(factoryPointer);
                    var itemId = GraphicsCaptureItemId;
                    IntPtr item;
                    interop.CreateForWindow(window, ref itemId, out item);
                    return item;
                }
                finally
                {
                    Marshal.Release(factoryPointer);
                }
            }
            finally
            {
                WindowsDeleteString(classId);
            }
        }

        private static IntPtr CreateFramePool(IntPtr device, SizeInt32 size)
        {
            const string className = "Windows.Graphics.Capture.Direct3D11CaptureFramePool";
            IntPtr classId;
            Marshal.ThrowExceptionForHR(WindowsCreateString(className, className.Length, out classId));
            try
            {
                var staticsId = FramePoolStatics2Id;
                IntPtr factory;
                Marshal.ThrowExceptionForHR(RoGetActivationFactory(classId, ref staticsId, out factory));
                try
                {
                    var create = GetVtableMethod<CreateFreeThreadedDelegate>(factory, 6);
                    IntPtr framePool;
                    var result = create(factory, device, 87, 2, size, out framePool);
                    Marshal.ThrowExceptionForHR(result);
                    return framePool;
                }
                finally
                {
                    Release(factory);
                }
            }
            finally
            {
                WindowsDeleteString(classId);
            }
        }

        private static void TryDisableCaptureBorder(IntPtr session)
        {
            try
            {
                var session3 = QueryInterface(session, GraphicsCaptureSession3Id);
                try
                {
                    var putBorderRequired = GetVtableMethod<PutBooleanDelegate>(session3, 7);
                    putBorderRequired(session3, 0);
                }
                finally
                {
                    Release(session3);
                }
            }
            catch
            {
                // Older Windows builds do not expose IGraphicsCaptureSession3.
            }
        }

        private static unsafe Bitmap SurfaceToBitmap(
            IntPtr surface,
            ID3D11Device device,
            ID3D11DeviceContext context)
        {
            var accessId = typeof(IDirect3DDxgiInterfaceAccess).GUID;
            IntPtr accessPointer;
            var queryResult = Marshal.QueryInterface(surface, ref accessId, out accessPointer);
            Marshal.ThrowExceptionForHR(queryResult);
            try
            {
                var access = (IDirect3DDxgiInterfaceAccess)Marshal.GetObjectForIUnknown(accessPointer);
                var textureId = typeof(ID3D11Texture2D).GUID;
                var texturePointer = access.GetInterface(ref textureId);
                using (var texture = new ID3D11Texture2D(texturePointer))
                {
                    var description = texture.Description;
                    var stagingDescription = new Texture2DDescription
                    {
                        Width = description.Width,
                        Height = description.Height,
                        MipLevels = 1,
                        ArraySize = 1,
                        Format = description.Format,
                        SampleDescription = new SampleDescription(1, 0),
                        Usage = ResourceUsage.Staging,
                        BindFlags = BindFlags.None,
                        CPUAccessFlags = CpuAccessFlags.Read,
                        MiscFlags = ResourceOptionFlags.None
                    };

                    using (var staging = device.CreateTexture2D(stagingDescription))
                    {
                        context.CopyResource(staging, texture);
                        var mapped = context.Map(staging, 0, MapMode.Read);
                        try
                        {
                            var bitmap = new Bitmap((int)description.Width, (int)description.Height, PixelFormat.Format32bppArgb);
                            var data = bitmap.LockBits(
                                new Rectangle(0, 0, bitmap.Width, bitmap.Height),
                                ImageLockMode.WriteOnly,
                                PixelFormat.Format32bppArgb);
                            try
                            {
                                for (var y = 0; y < bitmap.Height; y++)
                                {
                                    Buffer.MemoryCopy(
                                        (void*)(mapped.DataPointer + y * mapped.RowPitch),
                                        (void*)(data.Scan0 + y * data.Stride),
                                        data.Stride,
                                        bitmap.Width * 4);
                                }
                            }
                            finally
                            {
                                bitmap.UnlockBits(data);
                            }

                            return bitmap;
                        }
                        finally
                        {
                            context.Unmap(staging, 0);
                        }
                    }
                }
            }
            finally
            {
                Marshal.Release(accessPointer);
            }
        }

        private static T GetVtableMethod<T>(IntPtr pointer, int slot) where T : Delegate
        {
            var vtable = Marshal.ReadIntPtr(pointer);
            var method = Marshal.ReadIntPtr(vtable, slot * IntPtr.Size);
            return Marshal.GetDelegateForFunctionPointer<T>(method);
        }

        private static IntPtr QueryInterface(IntPtr pointer, Guid interfaceId)
        {
            var query = GetVtableMethod<QueryInterfaceDelegate>(pointer, 0);
            IntPtr result;
            Marshal.ThrowExceptionForHR(query(pointer, ref interfaceId, out result));
            return result;
        }

        private static void Release(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
            {
                return;
            }

            var release = GetVtableMethod<ReleaseDelegate>(pointer, 2);
            release(pointer);
        }

        private static void CloseAndRelease(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
            {
                return;
            }

            try
            {
                var closable = QueryInterface(pointer, ClosableId);
                try
                {
                    var close = GetVtableMethod<CloseDelegate>(closable, 6);
                    close(closable);
                }
                finally
                {
                    Release(closable);
                }
            }
            catch
            {
                // Some WinRT capture interfaces are not IClosable.
            }

            Release(pointer);
        }
    }
}
