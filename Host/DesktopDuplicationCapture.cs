using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace SimpleRemote.Host
{
    internal sealed class DesktopDuplicationCapture : IScreenCaptureBackend
    {
        private const int S_OK = 0;
        private const int DXGI_ERROR_WAIT_TIMEOUT = unchecked((int)0x887A0027);
        private const int DXGI_ERROR_ACCESS_LOST = unchecked((int)0x887A0026);
        private const int DXGI_ERROR_SESSION_DISCONNECTED = unchecked((int)0x887A0028);
        private const uint D3D11_SDK_VERSION = 7;
        private const uint D3D11_CPU_ACCESS_READ = 0x20000;
        private const uint D3D11_USAGE_STAGING = 3;
        private const uint DXGI_FORMAT_B8G8R8A8_UNORM = 87;

        private static readonly Guid Factory1Guid = new Guid("770AAE78-F26F-4DBA-A829-253C83D1B387");
        private static readonly Guid Texture2DGuid = new Guid("6F15AAF2-D208-4E89-9AB4-489535D34F9C");
        private static readonly ImageCodecInfo JpegCodec = ImageCodecInfo.GetImageEncoders().Length > 0
            ? Array.Find(ImageCodecInfo.GetImageEncoders(), codec => codec.FormatID == ImageFormat.Jpeg.Guid)
            : null;

        private readonly int _maxDimension;
        private readonly EncoderParameters _encoderParameters;

        private Rectangle _desktopBounds;
        private Bitmap _frameBitmap;
        private Bitmap _scaledBitmap;
        private Graphics _scaledGraphics;
        private MemoryStream _jpegStream;

        private IDXGIFactory1 _factory;
        private IDXGIAdapter1 _adapter;
        private IDXGIOutput1 _output;
        private IDXGIOutputDuplication _duplication;
        private ID3D11Device _device;
        private IntPtr _contextPtr;
        private IntPtr _stagingTexturePtr;
        private VTableCopyResource _copyResource;
        private VTableMap _map;
        private VTableUnmap _unmap;

        public DesktopDuplicationCapture(int maxDimension, long jpegQuality)
        {
            _maxDimension = maxDimension;
            _encoderParameters = new EncoderParameters(1);
            _encoderParameters.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, jpegQuality);

            Initialize();
        }

        public bool TryCaptureFrame(out int desktopWidth, out int desktopHeight, out byte[] jpegBytes)
        {
            desktopWidth = _desktopBounds.Width;
            desktopHeight = _desktopBounds.Height;
            jpegBytes = null;

            IDXGIResource desktopResource = null;
            IntPtr desktopTexturePtr = IntPtr.Zero;
            var frameAcquired = false;

            try
            {
                DXGI_OUTDUPL_FRAME_INFO frameInfo;
                var hr = _duplication.AcquireNextFrame(0, out frameInfo, out desktopResource);
                if (hr == DXGI_ERROR_WAIT_TIMEOUT)
                {
                    return false;
                }

                if (hr == DXGI_ERROR_ACCESS_LOST || hr == DXGI_ERROR_SESSION_DISCONNECTED)
                {
                    ResetDuplication();
                    return false;
                }

                Marshal.ThrowExceptionForHR(hr);
                frameAcquired = true;

                var resourceUnknown = Marshal.GetIUnknownForObject(desktopResource);
                try
                {
                    var textureGuid = Texture2DGuid;
                    Marshal.QueryInterface(resourceUnknown, ref textureGuid, out desktopTexturePtr);
                }
                finally
                {
                    Marshal.Release(resourceUnknown);
                }

                _copyResource(_contextPtr, _stagingTexturePtr, desktopTexturePtr);

                D3D11_MAPPED_SUBRESOURCE mapped;
                hr = _map(_contextPtr, _stagingTexturePtr, 0, 1, 0, out mapped);
                Marshal.ThrowExceptionForHR(hr);

                try
                {
                    CopyMappedSurface(mapped);
                }
                finally
                {
                    _unmap(_contextPtr, _stagingTexturePtr, 0);
                }

                if (_scaledGraphics != null)
                {
                    _scaledGraphics.DrawImage(_frameBitmap, new Rectangle(Point.Empty, _scaledBitmap.Size));
                }

                var imageToEncode = _scaledBitmap ?? _frameBitmap;
                _jpegStream.SetLength(0);

                if (JpegCodec != null)
                {
                    imageToEncode.Save(_jpegStream, JpegCodec, _encoderParameters);
                }
                else
                {
                    imageToEncode.Save(_jpegStream, ImageFormat.Jpeg);
                }

                jpegBytes = _jpegStream.ToArray();
                return true;
            }
            catch
            {
                ResetDuplication();
                return false;
            }
            finally
            {
                if (frameAcquired)
                {
                    try
                    {
                        _duplication.ReleaseFrame();
                    }
                    catch
                    {
                    }
                }

                if (desktopTexturePtr != IntPtr.Zero)
                {
                    Marshal.Release(desktopTexturePtr);
                }

                if (desktopResource != null)
                {
                    Marshal.ReleaseComObject(desktopResource);
                }
            }
        }

        public void Dispose()
        {
            DisposeResources();

            if (_encoderParameters != null)
            {
                _encoderParameters.Dispose();
            }
        }

        private void Initialize()
        {
            DisposeResources();

            if (Screen.AllScreens.Length != 1)
            {
                throw new NotSupportedException("Desktop Duplication backend is currently limited to single-monitor hosts.");
            }

            _desktopBounds = Screen.PrimaryScreen.Bounds;

            IntPtr factoryPtr;
            var factoryGuid = Factory1Guid;
            var hr = CreateDXGIFactory1(ref factoryGuid, out factoryPtr);
            Marshal.ThrowExceptionForHR(hr);

            try
            {
                _factory = (IDXGIFactory1)Marshal.GetObjectForIUnknown(factoryPtr);
            }
            finally
            {
                Marshal.Release(factoryPtr);
            }

            _adapter = FindAdapterForPrimaryOutput(_factory, _desktopBounds, out _output);
            if (_adapter == null || _output == null)
            {
                throw new InvalidOperationException("Could not locate a DXGI output for the primary screen.");
            }

            D3D_FEATURE_LEVEL featureLevel;
            hr = D3D11CreateDevice(_adapter, D3D_DRIVER_TYPE.UNKNOWN, IntPtr.Zero, 0, IntPtr.Zero, 0, D3D11_SDK_VERSION, out _device, out featureLevel, out _contextPtr);
            Marshal.ThrowExceptionForHR(hr);

            BindContextDelegates(_contextPtr);

            hr = _output.DuplicateOutput(_device, out _duplication);
            Marshal.ThrowExceptionForHR(hr);

            CreateStagingTexture();
            CreateBitmaps();
        }

        private void ResetDuplication()
        {
            try
            {
                Initialize();
            }
            catch
            {
            }
        }

        private void CreateStagingTexture()
        {
            var textureDesc = new D3D11_TEXTURE2D_DESC
            {
                Width = (uint)_desktopBounds.Width,
                Height = (uint)_desktopBounds.Height,
                MipLevels = 1,
                ArraySize = 1,
                Format = DXGI_FORMAT_B8G8R8A8_UNORM,
                SampleDesc = new DXGI_SAMPLE_DESC { Count = 1, Quality = 0 },
                Usage = D3D11_USAGE_STAGING,
                BindFlags = 0,
                CPUAccessFlags = D3D11_CPU_ACCESS_READ,
                MiscFlags = 0
            };

            var hr = _device.CreateTexture2D(ref textureDesc, IntPtr.Zero, out _stagingTexturePtr);
            Marshal.ThrowExceptionForHR(hr);
        }

        private void CreateBitmaps()
        {
            if (_frameBitmap != null)
            {
                _frameBitmap.Dispose();
                _frameBitmap = null;
            }

            if (_scaledGraphics != null)
            {
                _scaledGraphics.Dispose();
                _scaledGraphics = null;
            }

            if (_scaledBitmap != null)
            {
                _scaledBitmap.Dispose();
                _scaledBitmap = null;
            }

            if (_jpegStream != null)
            {
                _jpegStream.Dispose();
                _jpegStream = null;
            }

            _frameBitmap = new Bitmap(_desktopBounds.Width, _desktopBounds.Height, PixelFormat.Format32bppArgb);

            var scale = Math.Min(1.0, Math.Min((double)_maxDimension / _desktopBounds.Width, (double)_maxDimension / _desktopBounds.Height));
            if (scale < 0.999)
            {
                var scaledWidth = Math.Max(1, (int)Math.Round(_desktopBounds.Width * scale));
                var scaledHeight = Math.Max(1, (int)Math.Round(_desktopBounds.Height * scale));
                _scaledBitmap = new Bitmap(scaledWidth, scaledHeight, PixelFormat.Format24bppRgb);
                _scaledGraphics = Graphics.FromImage(_scaledBitmap);
                _scaledGraphics.CompositingMode = CompositingMode.SourceCopy;
                _scaledGraphics.CompositingQuality = CompositingQuality.HighSpeed;
                _scaledGraphics.InterpolationMode = InterpolationMode.Low;
                _scaledGraphics.PixelOffsetMode = PixelOffsetMode.HighSpeed;
                _scaledGraphics.SmoothingMode = SmoothingMode.None;
            }

            _jpegStream = new MemoryStream(_desktopBounds.Width * _desktopBounds.Height / 4);
        }

        private void CopyMappedSurface(D3D11_MAPPED_SUBRESOURCE mapped)
        {
            var rect = new Rectangle(0, 0, _frameBitmap.Width, _frameBitmap.Height);
            var bitmapData = _frameBitmap.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);

            try
            {
                for (var y = 0; y < _frameBitmap.Height; y++)
                {
                    var srcRow = new IntPtr(mapped.Data.ToInt64() + (long)(mapped.RowPitch * y));
                    var dstRow = new IntPtr(bitmapData.Scan0.ToInt64() + (long)(bitmapData.Stride * y));
                    CopyMemory(dstRow, srcRow, (uint)(Math.Min(bitmapData.Stride, _frameBitmap.Width * 4)));
                }
            }
            finally
            {
                _frameBitmap.UnlockBits(bitmapData);
            }
        }

        private void BindContextDelegates(IntPtr contextPtr)
        {
            _map = LoadVTableDelegate<VTableMap>(contextPtr, 10);
            _unmap = LoadVTableDelegate<VTableUnmap>(contextPtr, 11);
            _copyResource = LoadVTableDelegate<VTableCopyResource>(contextPtr, 43);
        }

        private static T LoadVTableDelegate<T>(IntPtr comObject, int methodIndex)
        {
            var vtable = Marshal.ReadIntPtr(comObject);
            var entry = Marshal.ReadIntPtr(vtable, methodIndex * IntPtr.Size);
            return (T)(object)Marshal.GetDelegateForFunctionPointer(entry, typeof(T));
        }

        private static IDXGIAdapter1 FindAdapterForPrimaryOutput(IDXGIFactory1 factory, Rectangle primaryBounds, out IDXGIOutput1 output1)
        {
            output1 = null;

            for (uint adapterIndex = 0; ; adapterIndex++)
            {
                IDXGIAdapter1 adapter;
                var hr = factory.EnumAdapters1(adapterIndex, out adapter);
                if (hr != S_OK)
                {
                    return null;
                }

                for (uint outputIndex = 0; ; outputIndex++)
                {
                    IDXGIOutput output;
                    hr = adapter.EnumOutputs(outputIndex, out output);
                    if (hr != S_OK)
                    {
                        break;
                    }

                    try
                    {
                        DXGI_OUTPUT_DESC desc;
                        output.GetDesc(out desc);
                        if (desc.AttachedToDesktop && desc.DesktopCoordinates.ToRectangle() == primaryBounds)
                        {
                            output1 = output as IDXGIOutput1;
                            if (output1 != null)
                            {
                                return adapter;
                            }
                        }
                    }
                    finally
                    {
                        if (output1 == null && output != null)
                        {
                            Marshal.ReleaseComObject(output);
                        }
                    }
                }

                Marshal.ReleaseComObject(adapter);
            }
        }

        private void DisposeResources()
        {
            if (_scaledGraphics != null)
            {
                _scaledGraphics.Dispose();
                _scaledGraphics = null;
            }

            if (_scaledBitmap != null)
            {
                _scaledBitmap.Dispose();
                _scaledBitmap = null;
            }

            if (_frameBitmap != null)
            {
                _frameBitmap.Dispose();
                _frameBitmap = null;
            }

            if (_jpegStream != null)
            {
                _jpegStream.Dispose();
                _jpegStream = null;
            }

            if (_stagingTexturePtr != IntPtr.Zero)
            {
                Marshal.Release(_stagingTexturePtr);
                _stagingTexturePtr = IntPtr.Zero;
            }

            if (_contextPtr != IntPtr.Zero)
            {
                Marshal.Release(_contextPtr);
                _contextPtr = IntPtr.Zero;
            }

            if (_duplication != null)
            {
                Marshal.ReleaseComObject(_duplication);
                _duplication = null;
            }

            if (_output != null)
            {
                Marshal.ReleaseComObject(_output);
                _output = null;
            }

            if (_adapter != null)
            {
                Marshal.ReleaseComObject(_adapter);
                _adapter = null;
            }

            if (_factory != null)
            {
                Marshal.ReleaseComObject(_factory);
                _factory = null;
            }

            if (_device != null)
            {
                Marshal.ReleaseComObject(_device);
                _device = null;
            }
        }

        [DllImport("dxgi.dll")]
        private static extern int CreateDXGIFactory1(ref Guid riid, out IntPtr ppFactory);

        [DllImport("d3d11.dll")]
        private static extern int D3D11CreateDevice(
            [MarshalAs(UnmanagedType.IUnknown)] object pAdapter,
            D3D_DRIVER_TYPE driverType,
            IntPtr software,
            uint flags,
            IntPtr pFeatureLevels,
            uint featureLevels,
            uint sdkVersion,
            [MarshalAs(UnmanagedType.Interface)] out ID3D11Device device,
            out D3D_FEATURE_LEVEL featureLevel,
            out IntPtr immediateContext);

        [DllImport("kernel32.dll", EntryPoint = "RtlMoveMemory", SetLastError = false)]
        private static extern void CopyMemory(IntPtr destination, IntPtr source, uint length);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int VTableMap(IntPtr thisPtr, IntPtr resource, uint subresource, uint mapType, uint mapFlags, out D3D11_MAPPED_SUBRESOURCE mappedSubresource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void VTableUnmap(IntPtr thisPtr, IntPtr resource, uint subresource);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate void VTableCopyResource(IntPtr thisPtr, IntPtr destinationResource, IntPtr sourceResource);

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_SAMPLE_DESC
        {
            public uint Count;
            public uint Quality;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_TEXTURE2D_DESC
        {
            public uint Width;
            public uint Height;
            public uint MipLevels;
            public uint ArraySize;
            public uint Format;
            public DXGI_SAMPLE_DESC SampleDesc;
            public uint Usage;
            public uint BindFlags;
            public uint CPUAccessFlags;
            public uint MiscFlags;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct D3D11_MAPPED_SUBRESOURCE
        {
            public IntPtr Data;
            public uint RowPitch;
            public uint DepthPitch;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_FRAME_INFO
        {
            public long LastPresentTime;
            public long LastMouseUpdateTime;
            public uint AccumulatedFrames;
            [MarshalAs(UnmanagedType.Bool)]
            public bool RectsCoalesced;
            [MarshalAs(UnmanagedType.Bool)]
            public bool ProtectedContentMaskedOut;
            public DXGI_OUTDUPL_POINTER_POSITION PointerPosition;
            public uint TotalMetadataBufferSize;
            public uint PointerShapeBufferSize;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DXGI_OUTDUPL_POINTER_POSITION
        {
            public POINT Position;
            [MarshalAs(UnmanagedType.Bool)]
            public bool Visible;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct POINT
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;

            public Rectangle ToRectangle()
            {
                return Rectangle.FromLTRB(Left, Top, Right, Bottom);
            }
        }

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct DXGI_OUTPUT_DESC
        {
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
            public string DeviceName;
            public RECT DesktopCoordinates;
            [MarshalAs(UnmanagedType.Bool)]
            public bool AttachedToDesktop;
            public uint Rotation;
            public IntPtr Monitor;
        }

        private enum D3D_DRIVER_TYPE : uint
        {
            UNKNOWN = 0
        }

        private enum D3D_FEATURE_LEVEL : uint
        {
        }

        [ComImport]
        [Guid("770AAE78-F26F-4DBA-A829-253C83D1B387")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIFactory1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] int EnumAdapters(uint adapter, out IntPtr dxgiAdapter);
            [PreserveSig] int MakeWindowAssociation(IntPtr windowHandle, uint flags);
            [PreserveSig] int GetWindowAssociation(out IntPtr windowHandle);
            [PreserveSig] int CreateSwapChain(IntPtr device, IntPtr desc, out IntPtr swapChain);
            [PreserveSig] int CreateSoftwareAdapter(IntPtr module, out IntPtr adapter);
            [PreserveSig] int EnumAdapters1(uint adapter, out IDXGIAdapter1 dxgiAdapter);
            [PreserveSig] bool IsCurrent();
        }

        [ComImport]
        [Guid("29038F61-3839-4626-91FD-086879011A05")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIAdapter1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] int EnumOutputs(uint output, out IDXGIOutput dxgiOutput);
            [PreserveSig] int GetDesc(out IntPtr desc);
            [PreserveSig] int CheckInterfaceSupport(ref Guid guid, out long umdVersion);
            [PreserveSig] int GetDesc1(out IntPtr desc1);
        }

        [ComImport]
        [Guid("AE02EEDB-C735-4690-8D52-5A8DC20213AA")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] void GetDesc(out DXGI_OUTPUT_DESC desc);
            [PreserveSig] int WaitForVBlank();
            [PreserveSig] int TakeOwnership(IntPtr device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            [PreserveSig] void ReleaseOwnership();
            [PreserveSig] int GetGammaControlCapabilities(out IntPtr caps);
            [PreserveSig] int SetGammaControl(IntPtr array);
            [PreserveSig] int GetGammaControl(out IntPtr array);
            [PreserveSig] int SetDisplaySurface(IntPtr scanoutSurface);
            [PreserveSig] int GetDisplaySurfaceData(IntPtr destination);
            [PreserveSig] int GetFrameStatistics(out IntPtr stats);
        }

        [ComImport]
        [Guid("00CDDEA8-939B-4B83-A340-A685226666CC")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutput1
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] void GetDesc(out DXGI_OUTPUT_DESC desc);
            [PreserveSig] int WaitForVBlank();
            [PreserveSig] int TakeOwnership(IntPtr device, [MarshalAs(UnmanagedType.Bool)] bool exclusive);
            [PreserveSig] void ReleaseOwnership();
            [PreserveSig] int GetGammaControlCapabilities(out IntPtr caps);
            [PreserveSig] int SetGammaControl(IntPtr array);
            [PreserveSig] int GetGammaControl(out IntPtr array);
            [PreserveSig] int SetDisplaySurface(IntPtr scanoutSurface);
            [PreserveSig] int GetDisplaySurfaceData(IntPtr destination);
            [PreserveSig] int GetFrameStatistics(out IntPtr stats);
            [PreserveSig] int GetDisplaySurfaceData1(IntPtr destination);
            [PreserveSig] int DuplicateOutput([MarshalAs(UnmanagedType.IUnknown)] object device, out IDXGIOutputDuplication duplication);
        }

        [ComImport]
        [Guid("191CFAC3-A341-470D-B26E-A864F428319C")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIOutputDuplication
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] void GetDesc(out IntPtr desc);
            [PreserveSig] int AcquireNextFrame(uint timeoutInMilliseconds, out DXGI_OUTDUPL_FRAME_INFO frameInfo, out IDXGIResource desktopResource);
            [PreserveSig] int GetFrameDirtyRects(uint dirtyRectsBufferSize, IntPtr dirtyRectsBuffer, out uint dirtyRectsBufferSizeRequired);
            [PreserveSig] int GetFrameMoveRects(uint moveRectsBufferSize, IntPtr moveRectBuffer, out uint moveRectsBufferSizeRequired);
            [PreserveSig] int GetFramePointerShape(uint pointerShapeBufferSize, IntPtr pointerShapeBuffer, out uint pointerShapeBufferSizeRequired, out IntPtr pointerShapeInfo);
            [PreserveSig] int MapDesktopSurface(out IntPtr lockedRect);
            [PreserveSig] int UnMapDesktopSurface();
            [PreserveSig] int ReleaseFrame();
        }

        [ComImport]
        [Guid("035F3AB4-482E-4E50-B41F-8A7F8BD8960B")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IDXGIResource
        {
            [PreserveSig] int SetPrivateData(ref Guid name, uint dataSize, IntPtr data);
            [PreserveSig] int SetPrivateDataInterface(ref Guid name, IntPtr unknown);
            [PreserveSig] int GetPrivateData(ref Guid name, ref uint dataSize, IntPtr data);
            [PreserveSig] int GetParent(ref Guid riid, out IntPtr parent);
            [PreserveSig] int GetSharedHandle(out IntPtr sharedHandle);
            [PreserveSig] int GetUsage(out uint usage);
            [PreserveSig] int SetEvictionPriority(uint evictionPriority);
            [PreserveSig] int GetEvictionPriority(out uint evictionPriority);
        }

        [ComImport]
        [Guid("DB6F6DDB-AC77-4E88-8253-819DF9BBF140")]
        [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ID3D11Device
        {
            [PreserveSig] int CreateBuffer(IntPtr desc, IntPtr initialData, out IntPtr buffer);
            [PreserveSig] int CreateTexture1D(IntPtr desc, IntPtr initialData, out IntPtr texture1D);
            [PreserveSig] int CreateTexture2D(ref D3D11_TEXTURE2D_DESC desc, IntPtr initialData, out IntPtr texture2D);
        }
    }
}
