using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Diagnostics;
namespace PrefireMacro
{
 // Windows DXGI/D3D11 interop. Slots and layouts checked against Microsoft WinSDK headers.
 // Only the UI tracking capture owns this interface; final input verification uses GDI.
 internal sealed class DesktopFrameCapture : IDisposable
 {
  [StructLayout(LayoutKind.Sequential)] internal struct Rect { internal int Left,Top,Right,Bottom; internal Rectangle Bounds {get{return Rectangle.FromLTRB(Left,Top,Right,Bottom);}} }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct OutputDesc {
   [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name; public Rect Area; public int Attached,Rotation; public IntPtr Monitor;
  }
  [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct OutputDesc1 {
   [MarshalAs(UnmanagedType.ByValTStr,SizeConst=32)] public string Name; public Rect Area; public int Attached,Rotation; public IntPtr Monitor;
   public uint Bits; public int ColorSpace; public float Rx,Ry,Gx,Gy,Bx,By,Wx,Wy,Min,Max,Full;
  }
  [StructLayout(LayoutKind.Sequential)] struct DesktopPoint { public int X,Y; }
  [StructLayout(LayoutKind.Sequential)] struct Pointer { public DesktopPoint Position; public int Visible; }
  [StructLayout(LayoutKind.Sequential)] struct FrameInfo { public long Present,Mouse; public uint Accumulated; public int Coalesced,Protected; public Pointer Pointer; public uint Metadata,Shape; }
  [StructLayout(LayoutKind.Sequential)] struct TextureDesc { public uint Width,Height,Mips,Array,Format,SampleCount,SampleQuality,Usage,Bind,Cpu,Misc; }
  [StructLayout(LayoutKind.Sequential)] struct Box { public uint Left,Top,Front,Right,Bottom,Back; }
  [StructLayout(LayoutKind.Sequential)] struct Mapped { public IntPtr Data; public uint Pitch,DepthPitch; }
  [DllImport("dxgi.dll")] static extern int CreateDXGIFactory1(ref Guid iid,out IntPtr factory);
  [DllImport("d3d11.dll")] static extern int D3D11CreateDevice(IntPtr adapter,int driver,IntPtr software,uint flags,IntPtr levels,uint count,uint sdk,out IntPtr device,out int feature,out IntPtr context);
  [DllImport("kernel32.dll",EntryPoint="RtlMoveMemory")] static extern void CopyMemory(IntPtr target,IntPtr source,UIntPtr bytes);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Enumerate(IntPtr self,uint index,out IntPtr item);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Describe(IntPtr self,out OutputDesc desc);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Describe1(IntPtr self,out OutputDesc1 desc);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Duplicate(IntPtr self,IntPtr device,out IntPtr duplication);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int Acquire(IntPtr self,uint timeout,out FrameInfo info,out IntPtr resource);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int EndFrame(IntPtr self);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int CreateTexture(IntPtr self,ref TextureDesc desc,IntPtr data,out IntPtr texture);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void CopyRegion(IntPtr self,IntPtr target,uint sub,uint x,uint y,uint z,IntPtr source,uint sourceSub,ref Box box);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate int MapTexture(IntPtr self,IntPtr texture,uint sub,int mode,uint flags,out Mapped mapped);
  [UnmanagedFunctionPointer(CallingConvention.StdCall)] delegate void UnmapTexture(IntPtr self,IntPtr texture,uint sub);
  static readonly Guid FactoryIid=new Guid("770aae78-f26f-4dba-a829-253c83d1b387"),Output1Iid=new Guid("00cddea8-939b-4b83-a340-a685226666cc"),Output6Iid=new Guid("068346e8-aaec-4b84-add7-137f513f77a1"),TextureIid=new Guid("6f15aaf2-d208-4e89-9ab4-489535d34f9c");
  IntPtr device,context,duplication,output6,staging;
  Size stagingSize; bool disposed;
  Acquire acquire; EndFrame end; Describe1 describe; CreateTexture create; CopyRegion copy; MapTexture map; UnmapTexture unmap;
  internal long ValidatedAtMs {get;private set;} internal long Version {get;private set;} internal string Status {get;private set;}
  internal Rectangle OutputBounds {get;private set;}
  static T Method<T>(IntPtr obj,int slot) where T:class {if(obj==IntPtr.Zero)throw new ArgumentException("Null COM interface");return (T)(object)Marshal.GetDelegateForFunctionPointer(Marshal.ReadIntPtr(Marshal.ReadIntPtr(obj),slot*IntPtr.Size),typeof(T));}
  static void Check(int hr,string action){if(hr<0)throw new ExternalException(action,hr);}
  static void Release(ref IntPtr obj){IntPtr value=obj;obj=IntPtr.Zero;if(value!=IntPtr.Zero)Marshal.Release(value);}
  static IntPtr Query(IntPtr obj,Guid iid){IntPtr result;Check(Marshal.QueryInterface(obj,ref iid,out result),"QueryInterface "+iid);return result;}
  internal DesktopFrameCapture(Rectangle area)
  {
   if(IntPtr.Size!=8||Marshal.SizeOf(typeof(OutputDesc))!=96||Marshal.SizeOf(typeof(OutputDesc1))!=152||Marshal.SizeOf(typeof(FrameInfo))!=48||Marshal.SizeOf(typeof(TextureDesc))!=44||Marshal.SizeOf(typeof(Mapped))!=16)throw new NotSupportedException("Unexpected native layout");
   IntPtr factory=IntPtr.Zero,adapter=IntPtr.Zero,output=IntPtr.Zero,output1=IntPtr.Zero;
   try {
    Guid factoryId=FactoryIid;Check(CreateDXGIFactory1(ref factoryId,out factory),"CreateDXGIFactory1");Enumerate adapters=Method<Enumerate>(factory,12);
    bool selected=false;
    for(uint a=0;!selected;a++){
     int hr=adapters(factory,a,out adapter);if(hr==unchecked((int)0x887A0002))break;Check(hr,"EnumAdapters1");
     Enumerate outputs=Method<Enumerate>(adapter,7);
     for(uint o=0;!selected;o++){
      hr=outputs(adapter,o,out output);if(hr==unchecked((int)0x887A0002))break;Check(hr,"EnumOutputs");OutputDesc desc;Check(Method<Describe>(output,7)(output,out desc),"GetDesc");
      if(desc.Attached!=0&&desc.Rotation==1&&desc.Area.Bounds.Contains(area)){
       output6=Query(output,Output6Iid);describe=Method<Describe1>(output6,27);OutputDesc1 extended;Check(describe(output6,out extended),"GetDesc1");
       if(extended.ColorSpace!=0)throw new NotSupportedException("HDR/wide-color output requires the compatibility capture path");
       int feature;Check(D3D11CreateDevice(adapter,0,IntPtr.Zero,0x20,IntPtr.Zero,0,7,out device,out feature,out context),"D3D11CreateDevice");
       output1=Query(output,Output1Iid);Check(Method<Duplicate>(output1,22)(output1,device,out duplication),"DuplicateOutput");OutputBounds=desc.Area.Bounds;selected=true;
      }
      Release(ref output);
     }
     Release(ref adapter);
    }
    if(!selected)throw new NotSupportedException("Capture area does not fit one connected unrotated output");
    acquire=Method<Acquire>(duplication,8);end=Method<EndFrame>(duplication,14);create=Method<CreateTexture>(device,5);copy=Method<CopyRegion>(context,46);map=Method<MapTexture>(context,14);unmap=Method<UnmapTexture>(context,15);
   }catch{Dispose();throw;}finally{Release(ref output1);Release(ref output);Release(ref adapter);Release(ref factory);}
  }
  void Prepare(Rectangle area)
  {
   if(staging!=IntPtr.Zero&&stagingSize==area.Size)return;
   Release(ref staging);
   TextureDesc desc=new TextureDesc{Width=(uint)area.Width,Height=(uint)area.Height,Mips=1,Array=1,Format=87,SampleCount=1,Usage=3,Cpu=0x20000};
   Check(create(device,ref desc,IntPtr.Zero,out staging),"CreateTexture2D");stagingSize=area.Size;
  }
  internal static bool RecentPresentation(long presented, long now, long notBefore)
  { return presented > 0 && presented > notBefore && now >= presented && now - presented <= 12; }
  internal bool Capture(Rectangle area,Bitmap target,long notBefore)
  {
   if(disposed)throw new ObjectDisposedException("DesktopFrameCapture");ValidatedAtMs=0;
   if(area.Width<=0||area.Height<=0||target.Size!=area.Size||target.PixelFormat!=PixelFormat.Format32bppRgb)throw new ArgumentException("Capture area/format");
   OutputDesc1 current;Check(describe(output6,out current),"Output state");if(current.Attached==0||current.Rotation!=1||current.ColorSpace!=0||current.Area.Bounds!=OutputBounds||!OutputBounds.Contains(area))throw new NotSupportedException("Display geometry/color mode changed");
   Prepare(area);IntPtr resource=IntPtr.Zero,texture=IntPtr.Zero;bool acquired=false,mapped=false;
   try {
    FrameInfo info;int hr=acquire(duplication,0,out info,out resource);long validated=(long)(Stopwatch.GetTimestamp()*(1000.0/Stopwatch.Frequency));
    if(hr==unchecked((int)0x887A0027)){
     // No new surface is not a newly captured image. The caller must retry or
     // obtain a compatibility screenshot, never confirm a shot from old pixels.
     Status="NoNewFrame";return false;
    }
    Check(hr,"AcquireNextFrame");acquired=true;if(info.Protected!=0)throw new NotSupportedException("Protected desktop content");
    long presented=(long)(info.Present*(1000.0/Stopwatch.Frequency));
    if(!RecentPresentation(presented,validated,notBefore)){Status="NoRecentPresentation";return false;}
    texture=Query(resource,TextureIid);
    Box box=new Box{Left=(uint)(area.Left-OutputBounds.Left),Top=(uint)(area.Top-OutputBounds.Top),Right=(uint)(area.Right-OutputBounds.Left),Bottom=(uint)(area.Bottom-OutputBounds.Top),Back=1};
    copy(context,staging,0,0,0,0,texture,0,ref box);Mapped data;Check(map(context,staging,0,1,0,out data),"Map staging texture");mapped=true;
    if(data.Data==IntPtr.Zero||data.Pitch<(uint)area.Width*4||data.Pitch>Int32.MaxValue)throw new InvalidOperationException("Invalid mapped row pitch");
    BitmapData dest=target.LockBits(new Rectangle(Point.Empty,area.Size),ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);
    try{for(int y=0;y<area.Height;y++)CopyMemory(IntPtr.Add(dest.Scan0,y*dest.Stride),IntPtr.Add(data.Data,checked(y*(int)data.Pitch)),new UIntPtr((uint)area.Width*4));}finally{target.UnlockBits(dest);}
    if(!RecentPresentation(presented,(long)(Stopwatch.GetTimestamp()*(1000.0/Stopwatch.Frequency)),notBefore)){Status="ReadbackTooOld";return false;}
    Version++;ValidatedAtMs=presented;Status="NewDesktopFrame";return true;
   }finally{if(mapped)unmap(context,staging,0);Release(ref texture);Release(ref resource);if(acquired)Check(end(duplication),"ReleaseFrame");}
  }
  public void Dispose(){if(disposed)return;disposed=true;Release(ref staging);Release(ref duplication);Release(ref output6);Release(ref context);Release(ref device);}
 }
}
