using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
namespace PrefireMacro {
 // One worker owns all DXGI interfaces; consumers receive only private RGB copies.
 internal sealed class AsyncDesktopSource : IDisposable, IRecentHudFrames {
  internal interface IFrameSource:IDisposable { bool Capture(Rectangle area,Bitmap target,long after); long Timestamp {get;} string State {get;} }
  sealed class NativeSource:IFrameSource {
   readonly DesktopFrameCapture source;internal NativeSource(Rectangle area){source=new DesktopFrameCapture(area);}
   public bool Capture(Rectangle area,Bitmap target,long after){return source.Capture(area,target,after);}
   public long Timestamp{get{return source.ValidatedAtMs;}}public string State{get{return source.Status;}}public void Dispose(){source.Dispose();}
  }
  internal sealed class Request {
   internal readonly IntPtr Window; internal readonly uint Process; internal readonly Rectangle Client,Area; internal readonly long Generation,Started;
   internal Request(IntPtr window,uint process,Rectangle client,Rectangle area,long generation,long started){Window=window;Process=process;Client=client;Area=area;Generation=generation;Started=started;}
   internal bool Same(IntPtr w,uint p,Rectangle c,Rectangle a){return Window==w&&Process==p&&Client==c&&Area==a;}
  }
  readonly object gate=new object(); readonly AutoResetEvent changed=new AutoResetEvent(false); readonly Thread worker;
  readonly Func<Request,bool> allowed;
  readonly Func<Rectangle,IFrameSource> factory;
  readonly Func<long> imageClock;
  Request wanted,publishedRequest; Bitmap published; long presented,consumed,generation,heartbeat;
  bool disposed; string status="Idle";
  internal string LastReadStatus="None";
  internal string Status {get{lock(gate)return status;}}
  public bool WaitingForNewFrames {get{lock(gate)return !disposed&&wanted!=null&&(status=="Ready"||status=="NoNewFrame"||status=="NoRecentPresentation"||status=="ReadbackTooOld");}}
  internal long FramesProduced,FramesCopied,FramesRejected;
  [DllImport("kernel32.dll",EntryPoint="RtlMoveMemory")]static extern void CopyMemory(IntPtr target,IntPtr source,UIntPtr count);
  internal static long Clock(){return (long)(Stopwatch.GetTimestamp()*(1000.0/Stopwatch.Frequency));}
  internal AsyncDesktopSource(Func<Request,bool> validator,Func<Rectangle,IFrameSource> sourceFactory=null,Func<long> presentationClock=null){allowed=validator;imageClock=presentationClock??Clock;factory=sourceFactory??delegate(Rectangle r){return new NativeSource(r);};worker=new Thread(Run){IsBackground=true,Name="Prefire capture prototype"};worker.Start();}
  internal bool WorkerStopped {get{return !worker.IsAlive;}}
  internal void Configure(IntPtr window,uint process,Rectangle client,Rectangle area){
   lock(gate){if(disposed)throw new ObjectDisposedException("AsyncDesktopSource");
    if(wanted==null||!wanted.Same(window,process,client,area)){wanted=new Request(window,process,client,area,++generation,imageClock());published=null;publishedRequest=null;presented=consumed=0;status="Preparing";}
    heartbeat=Clock();changed.Set();
   }
  }
  internal void Stop(){lock(gate){if(disposed)return;wanted=null;published=null;publishedRequest=null;presented=consumed=0;generation++;status="Idle";changed.Set();}}
  public bool TryRead(IntPtr window,uint process,Rectangle client,Rectangle area,Bitmap target,long notBefore,out long timestamp){
   lock(gate){if(wanted==null||!wanted.Same(window,process,client,area)){timestamp=0;return false;}return TryRead(target,notBefore,out timestamp);}
  }
  internal bool TryRead(Bitmap target,long notBefore,out long timestamp){
   timestamp=0;lock(gate){
    if(disposed||wanted==null||published==null||!Object.ReferenceEquals(wanted,publishedRequest)||presented<=consumed||target.Size!=wanted.Area.Size||target.PixelFormat!=PixelFormat.Format32bppRgb){LastReadStatus="Unavailable: disposed="+disposed+", wanted="+(wanted!=null)+", published="+(published!=null)+", consumed="+consumed+", presented="+presented;return false;}
    if(!allowed(wanted)||!DesktopFrameCapture.RecentPresentation(presented,imageClock(),notBefore)){LastReadStatus="AgeOrIdentityBeforeCopy: age="+(imageClock()-presented)+", after="+notBefore;FramesRejected++;return false;}
    BitmapData from=published.LockBits(new Rectangle(Point.Empty,published.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppRgb);
    try{BitmapData to=target.LockBits(new Rectangle(Point.Empty,target.Size),ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);
     try{for(int y=0;y<target.Height;y++)CopyMemory(IntPtr.Add(to.Scan0,y*to.Stride),IntPtr.Add(from.Scan0,y*from.Stride),new UIntPtr((uint)target.Width*4));}finally{target.UnlockBits(to);}
    }finally{published.UnlockBits(from);}
    // A slow consumer copy must not turn an expired producer frame into a fresh observation.
    if(!allowed(wanted)||!DesktopFrameCapture.RecentPresentation(presented,imageClock(),notBefore)){LastReadStatus="AgeOrIdentityAfterCopy";FramesRejected++;return false;}
    consumed=presented;timestamp=presented;FramesCopied++;LastReadStatus="Copied";return true;
   }
  }
  void SetStatus(string text){lock(gate)status=text;}
  void Run(){IFrameSource source=null;Bitmap[] buffers=null;Request active=null;long last=0,next=0,retryAt=0;int slot=0;
   Action clear=delegate{lock(gate){published=null;publishedRequest=null;presented=0;if(buffers!=null){foreach(Bitmap image in buffers)image.Dispose();buffers=null;}}};
   try{while(true){Request request;long beat;lock(gate){if(disposed)break;request=wanted;beat=heartbeat;}
    if(request==null||Clock()-beat>250){if(active!=null){clear();if(source!=null){source.Dispose();source=null;}active=null;}changed.WaitOne(20);continue;}
    if(Clock()<retryAt){changed.WaitOne((int)Math.Min(50,Math.Max(1,retryAt-Clock())));continue;}
    if(Clock()<next){changed.WaitOne((int)Math.Max(1,next-Clock()));continue;}
    try{
     if(!allowed(request)){clear();if(source!=null){source.Dispose();source=null;}active=null;SetStatus("ForegroundOrGeometryChanged");changed.WaitOne(10);continue;}
     if(!Object.ReferenceEquals(active,request)){
      clear();if(source!=null){source.Dispose();source=null;}source=factory(request.Area);
      buffers=new Bitmap[]{new Bitmap(request.Area.Width,request.Area.Height,PixelFormat.Format32bppRgb),new Bitmap(request.Area.Width,request.Area.Height,PixelFormat.Format32bppRgb)};active=request;slot=0;last=0;
     }
     next=Clock()+4;bool captured=source.Capture(request.Area,buffers[slot],Math.Max(last,request.Started));
     if(!captured){SetStatus(source.State);continue;}
     if(!allowed(request)){clear();if(source!=null){source.Dispose();source=null;}active=null;SetStatus("ForegroundOrGeometryChanged");continue;}
     lock(gate){if(disposed||!Object.ReferenceEquals(wanted,request)||Clock()-heartbeat>250)continue;
      if(!DesktopFrameCapture.RecentPresentation(source.Timestamp,imageClock(),Math.Max(last,request.Started))){FramesRejected++;continue;}
      last=source.Timestamp;published=buffers[slot];publishedRequest=request;presented=last;FramesProduced++;slot=1-slot;status="Ready";
     }
    }catch(Exception ex){clear();if(source!=null){source.Dispose();source=null;}active=null;SetStatus("Unavailable: "+ex.GetType().Name);retryAt=Clock()+5000;}
   }}finally{clear();if(source!=null)source.Dispose();lock(gate){changed.Dispose();}}
  }
  public void Dispose(){lock(gate){if(disposed)return;disposed=true;wanted=null;published=null;publishedRequest=null;changed.Set();}if(Thread.CurrentThread!=worker)worker.Join(2000);}
 }
}
