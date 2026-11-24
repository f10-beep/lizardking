// SPDX-License-Identifier: AGPL-3.0-or-later
//
// How the Guide's windows reach the screen: drawn with GDI+ into a bitmap
// (a few milliseconds of CPU), then handed to the compositor through our own
// Direct3D swap chain and a DirectComposition visual.
//
// Not through the window's own GDI surface: Windows copies a painted window
// into the compositor on the GPU, at no priority of ours, and with a game
// keeping the GPU busy behind another window (a browser over Elite) a single
// paint waited 1-10 s. Our own swap chain is our GPU work, so it can ask for
// the realtime scheduling class: measured over Elite, a key map change
// reached the screen in about 50 ms, where the normal and high classes still
// waited seconds. The work it puts ahead of the game is one copy per change.
//
// Direct3D, DXGI and DirectComposition are COM: called here through their
// vtables, the slot numbers from the Windows SDK headers, so the Guide still
// builds with the .NET Framework's csc alone.

using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace LizardKing
{
	static class Com
	{
		public static T Fn<T>(IntPtr obj, int slot) where T : class
		{
			IntPtr vt = Marshal.ReadIntPtr(obj);
			IntPtr f = Marshal.ReadIntPtr(vt, slot * IntPtr.Size);
			return (T)(object)Marshal.GetDelegateForFunctionPointer(
				f, typeof(T));
		}

		public static void Check(int hr, string what)
		{
			if (hr < 0)
				throw new COMException(what, hr);
		}

		public static IntPtr Query(IntPtr obj, Guid iid)
		{
			IntPtr o;
			Check(Marshal.QueryInterface(obj, ref iid, out o),
			      "QueryInterface");
			return o;
		}

		public static void Release(ref IntPtr p)
		{
			if (p != IntPtr.Zero)
				Marshal.Release(p);
			p = IntPtr.Zero;
		}
	}

	[StructLayout(LayoutKind.Sequential)]
	struct SwapChainDesc1
	{
		public uint Width, Height;
		public int Format, Stereo;
		public uint SampleCount, SampleQuality, BufferUsage, BufferCount;
		public int Scaling, SwapEffect, AlphaMode;
		public uint Flags;
	}

	// One Direct3D device for the process, and the compositor's: opened on
	// first use, and again after it is lost (a driver update or reset)
	static class Gpu
	{
		static readonly Guid IidDxgiDevice =
			new Guid("54ec77fa-1377-44e6-8c32-88fd5f44c84c");
		static readonly Guid IidFactory2 =
			new Guid("50c83a1c-e072-4c48-87b0-3630fa36a6d0");
		static readonly Guid IidDCompDevice =
			new Guid("c37ea93a-e7aa-450d-b16f-9746cb0407f3");

		public static IntPtr Device, Context, Factory, DComp;
		// counts losses: a Surface from an earlier generation is on a
		// lost device
		public static int Generation;
		static int lostAt;
		static bool lost;

		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int GetAdapterFn(IntPtr self, out IntPtr adapter);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int GetParentFn(IntPtr self, ref Guid iid,
					 out IntPtr parent);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int CommitFn(IntPtr self);

		[DllImport("d3d11.dll")]
		static extern int D3D11CreateDevice(IntPtr adapter, int driver,
						    IntPtr software, uint flags,
						    IntPtr levels, uint nLevels,
						    uint sdk, out IntPtr device,
						    out int level, out IntPtr ctx);
		[DllImport("dcomp.dll")]
		static extern int DCompositionCreateDevice(IntPtr dxgiDevice,
							   ref Guid iid,
							   out IntPtr dcomp);
		[DllImport("gdi32.dll")]
		static extern int D3DKMTSetProcessSchedulingPriorityClass(
			IntPtr process, int cls);
		[DllImport("kernel32.dll")]
		static extern IntPtr GetCurrentProcess();

		// Whether there is a device, opening it if need be. A GPU that has
		// gone stays gone a while: opening it costs the window thread a
		// few hundred ms, too much for every paint.
		public static bool Open()
		{
			if (Device != IntPtr.Zero)
				return true;
			if (lost && Environment.TickCount - lostAt < 2000)
				return false;
			try {
				Create();
				return true;
			} catch (Exception) {
				Lost();
				return false;
			}
		}

		// after a failure: everything goes, and Open starts afresh
		public static void Lost()
		{
			Com.Release(ref DComp);
			Com.Release(ref Factory);
			Com.Release(ref Context);
			Com.Release(ref Device);
			Generation++;
			lost = true;
			lostAt = Environment.TickCount;
		}

		// IDCompositionDevice::Commit (3)
		public static void Commit()
		{
			Com.Check(Com.Fn<CommitFn>(DComp, 3)(DComp), "Commit");
		}

		static void Create()
		{
			// a hardware device (1), else Windows' software one (WARP,
			// 5: a VM, a remote session, the basic display driver),
			// that GDI-format BGRA (0x20) works with, at the runtime's
			// SDK version (7)
			int level;
			if (D3D11CreateDevice(IntPtr.Zero, 1, IntPtr.Zero, 0x20,
					      IntPtr.Zero, 0, 7, out Device,
					      out level, out Context) < 0)
				Com.Check(D3D11CreateDevice(IntPtr.Zero, 5,
							    IntPtr.Zero, 0x20,
							    IntPtr.Zero, 0, 7,
							    out Device, out level,
							    out Context),
					  "D3D11CreateDevice");
			IntPtr dxgi = Com.Query(Device, IidDxgiDevice);
			IntPtr adapter = IntPtr.Zero;
			// released on failure too: opening is retried every few
			// seconds while it fails, and each would leak a device
			try {
				// IDXGIDevice::GetAdapter (7), IDXGIObject::GetParent
				// (6)
				Com.Check(Com.Fn<GetAdapterFn>(dxgi, 7)(dxgi,
									out adapter),
					  "GetAdapter");
				Guid f2 = IidFactory2;
				Com.Check(Com.Fn<GetParentFn>(adapter, 6)(
						  adapter, ref f2, out Factory),
					  "GetParent");
				Guid dc = IidDCompDevice;
				Com.Check(DCompositionCreateDevice(dxgi, ref dc,
								   out DComp),
					  "DCompositionCreateDevice");
			} finally {
				Com.Release(ref adapter);
				Com.Release(ref dxgi);
			}
			// REALTIME (5), else HIGH (4): our copies go ahead of a
			// game's frames instead of queueing behind them
			if (D3DKMTSetProcessSchedulingPriorityClass(
				    GetCurrentProcess(), 5) != 0)
				D3DKMTSetProcessSchedulingPriorityClass(
					GetCurrentProcess(), 4);
		}
	}

	// A window's picture: Show(bitmap) puts a premultiplied-alpha bitmap
	// on screen at the window's position, at the opacity given, and Hide()
	// takes it away. The window needs WS_EX_NOREDIRECTIONBITMAP, so
	// Windows keeps no GDI surface for it at all. Made once Gpu.Open has a
	// device.
	class Surface
	{
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int CreateTargetFn(IntPtr self, IntPtr hwnd, int topmost,
					    out IntPtr target);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int CreateFn(IntPtr self, out IntPtr obj);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int SetFn(IntPtr self, IntPtr content);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int SetFloatFn(IntPtr self, float value);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int CreateSwapChainFn(IntPtr self, IntPtr device,
					       ref SwapChainDesc1 desc,
					       IntPtr restrict, out IntPtr sc);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int ResizeBuffersFn(IntPtr self, uint count, uint w,
					     uint h, int format, uint flags);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int GetBufferFn(IntPtr self, uint i, ref Guid iid,
					 out IntPtr surface);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate int PresentFn(IntPtr self, uint sync, uint flags);
		[UnmanagedFunctionPointer(CallingConvention.StdCall)]
		delegate void UpdateFn(IntPtr self, IntPtr dst, uint sub,
				       IntPtr box, IntPtr src, uint rowPitch,
				       uint depthPitch);

		const int Bgra = 87;
		static readonly Guid IidTexture2D =
			new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
		IntPtr target, visual, effect, chain;
		readonly int generation;
		int width, height;
		bool shown;

		public Surface(IntPtr hwnd, double opacity)
		{
			generation = Gpu.Generation;
			try {
				Build(hwnd, opacity);
			} catch (Exception) {
				// a window takes one composition target: a half-built
				// one left behind would keep it blank for good
				Dispose();
				throw;
			}
		}

		void Build(IntPtr hwnd, double opacity)
		{
			// IDCompositionDevice: CreateTargetForHwnd (6),
			// CreateVisual (7); IDCompositionTarget::SetRoot (3)
			Com.Check(Com.Fn<CreateTargetFn>(Gpu.DComp, 6)(
					  Gpu.DComp, hwnd, 1, out target),
				  "CreateTargetForHwnd");
			Com.Check(Com.Fn<CreateFn>(Gpu.DComp, 7)(
					  Gpu.DComp, out visual),
				  "CreateVisual");
			Com.Check(Com.Fn<SetFn>(target, 3)(target, visual),
				  "SetRoot");
			// IDCompositionDevice::CreateEffectGroup (23),
			// IDCompositionEffectGroup::SetOpacity(float) (4; 3 is
			// the overload taking an animation),
			// IDCompositionVisual::SetEffect (10)
			Com.Check(Com.Fn<CreateFn>(Gpu.DComp, 23)(
					  Gpu.DComp, out effect),
				  "CreateEffectGroup");
			Com.Check(Com.Fn<SetFloatFn>(effect, 4)(effect,
								(float)opacity),
				  "SetOpacity");
			Com.Check(Com.Fn<SetFn>(visual, 10)(visual, effect),
				  "SetEffect");
		}

		// IDCompositionVisual::SetContent (15), committed
		void Content(IntPtr content)
		{
			Com.Check(Com.Fn<SetFn>(visual, 15)(visual, content),
				  "SetContent");
			Gpu.Commit();
		}

		void Fit(int w, int h)
		{
			if (chain != IntPtr.Zero && w == width && h == height)
				return;
			if (chain == IntPtr.Zero) {
				// render-target output (0x20), two buffers,
				// stretch (0), flip sequential (3), premultiplied
				// alpha (1): what composition swap chains take
				var d = new SwapChainDesc1 {
					Width = (uint)w,
					Height = (uint)h,
					Format = Bgra,
					SampleCount = 1,
					BufferUsage = 0x20,
					BufferCount = 2,
					SwapEffect = 3,
					AlphaMode = 1,
				};
				// IDXGIFactory2::CreateSwapChainForComposition (24)
				Com.Check(Com.Fn<CreateSwapChainFn>(Gpu.Factory, 24)(
						  Gpu.Factory, Gpu.Device, ref d,
						  IntPtr.Zero, out chain),
					  "CreateSwapChainForComposition");
			} else {
				// IDXGISwapChain::ResizeBuffers (13)
				Com.Check(Com.Fn<ResizeBuffersFn>(chain, 13)(
						  chain, 2, (uint)w, (uint)h, Bgra,
						  0),
					  "ResizeBuffers");
			}
			width = w;
			height = h;
		}

		public void Show(Bitmap bmp)
		{
			Fit(bmp.Width, bmp.Height);
			var data = bmp.LockBits(new Rectangle(Point.Empty, bmp.Size),
						ImageLockMode.ReadOnly,
						PixelFormat.Format32bppPArgb);
			IntPtr tex;
			Guid iid = IidTexture2D;
			try {
				// IDXGISwapChain::GetBuffer (9); the immediate
				// context's UpdateSubresource (48)
				Com.Check(Com.Fn<GetBufferFn>(chain, 9)(
						  chain, 0, ref iid, out tex),
					  "GetBuffer");
				Com.Fn<UpdateFn>(Gpu.Context, 48)(
					Gpu.Context, tex, 0, IntPtr.Zero,
					data.Scan0, (uint)data.Stride, 0);
				Com.Release(ref tex);
			} finally {
				bmp.UnlockBits(data);
			}
			// IDXGISwapChain::Present (8)
			Com.Check(Com.Fn<PresentFn>(chain, 8)(chain, 1, 0),
				  "Present");
			if (!shown) {
				Content(chain);
				shown = true;
			}
		}

		public void Hide()
		{
			if (!shown)
				return;
			Content(IntPtr.Zero);
			shown = false;
		}

		// made on a device since lost
		public bool Stale
		{
			get { return generation != Gpu.Generation; }
		}

		public void Dispose()
		{
			Com.Release(ref chain);
			Com.Release(ref effect);
			Com.Release(ref visual);
			Com.Release(ref target);
		}
	}
}
