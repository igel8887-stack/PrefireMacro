using System;
using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System.Threading;
using System.IO;
using PrefireMacro;
class OwnershipTests {
 sealed class Frame:IDisposable {internal int Number;internal int Age;internal bool Fail;internal readonly ManualResetEvent Entered=new ManualResetEvent(false),Permit=new ManualResetEvent(true),Done=new ManualResetEvent(false);public void Dispose(){Entered.Dispose();Permit.Dispose();Done.Dispose();}}
 sealed class Factory {
  internal readonly BlockingCollection<Frame> Pending=new BlockingCollection<Frame>();internal int Created,Disposed,Enabled=1;internal long ImageTime=1000;
  internal long Now(){return Interlocked.Read(ref ImageTime);}
  internal AsyncDesktopSource.IFrameSource New(Rectangle area){Interlocked.Increment(ref Created);return new Source(this);}
  internal bool Allowed(AsyncDesktopSource.Request r){return Volatile.Read(ref Enabled)==1;}
 }
 sealed class Source:AsyncDesktopSource.IFrameSource {
  readonly Factory owner;public long Timestamp{get;private set;} public string State{get;private set;}
  internal Source(Factory f){owner=f;}
  public bool Capture(Rectangle area,Bitmap image,long after){Timestamp=0;Frame f;if(!owner.Pending.TryTake(out f,1)){State="NoNewFrame";return false;}f.Entered.Set();try{if(!f.Permit.WaitOne(1000))throw new Exception("Scripted frame never released");if(f.Fail){State="NoNewFrame";return false;}
   BitmapData data=image.LockBits(new Rectangle(Point.Empty,image.Size),ImageLockMode.WriteOnly,PixelFormat.Format32bppRgb);int[] row=new int[image.Width];try{for(int y=0;y<image.Height;y++){for(int x=0;x<row.Length;x++)row[x]=Pixel(f.Number,x,y);Marshal.Copy(row,0,IntPtr.Add(data.Scan0,y*data.Stride),row.Length);}}finally{image.UnlockBits(data);}
   Timestamp=Interlocked.Add(ref owner.ImageTime,5)-f.Age;State="Ready";return true;
  }finally{f.Done.Set();}}
  public void Dispose(){Interlocked.Increment(ref owner.Disposed);}
 }
 static int Pixel(int n,int x,int y){return ((n*31+x)%256<<16)|((n*47+y)%256<<8)|((n*71+x+y)%256);}
 static void Check(bool v,string m){if(!v)throw new Exception(m);checks++;}static int checks;
 static void Configure(AsyncDesktopSource s,Rectangle area){s.Configure(new IntPtr(123),42,new Rectangle(0,0,2560,1440),area);}
 static long Consume(AsyncDesktopSource s,Bitmap image,long after){Stopwatch wait=Stopwatch.StartNew();while(wait.ElapsedMilliseconds<500){long at;if(s.TryRead(image,after,out at))return at;Thread.Sleep(1);}throw new Exception("Consumer timeout: "+s.Status+" / "+s.LastReadStatus);}
 static void Pixels(Bitmap image,int n){BitmapData data=image.LockBits(new Rectangle(Point.Empty,image.Size),ImageLockMode.ReadOnly,PixelFormat.Format32bppRgb);int[] row=new int[image.Width];try{for(int y=0;y<image.Height;y++){Marshal.Copy(IntPtr.Add(data.Scan0,y*data.Stride),row,0,row.Length);for(int x=0;x<row.Length;x++)if((row[x]&0xffffff)!=Pixel(n,x,y))throw new Exception("Mixed or incorrect image at "+x+","+y);}}finally{image.UnlockBits(data);}checks++;}
 static int Main(string[] args){Directory.CreateDirectory(args[0]);Factory f=new Factory();AsyncDesktopSource s=new AsyncDesktopSource(f.Allowed,f.New,f.Now);
  try{Rectangle area=new Rectangle(10,20,320,180);Configure(s,area);Thread.Sleep(5);long last=0;
   using(Bitmap image=new Bitmap(320,180,PixelFormat.Format32bppRgb)){
    for(int n=1;n<=40;n++)using(Frame frame=new Frame{Number=n}){Configure(s,area);f.Pending.Add(frame);last=Consume(s,image,last);Check(frame.Done.WaitOne(500),"Producer completed frame");Pixels(image,n);long none;Check(!s.TryRead(image,0,out none)&&none==0,"Same producer image cannot be consumed twice");Check(image.GetPixel(0,0).A==255,"Raw zero alpha remains opaque RGB");}
    using(Frame stale=new Frame{Number=41,Age=50}){long before=s.FramesProduced;f.Pending.Add(stale);Check(stale.Done.WaitOne(500),"Stale frame completed");Thread.Sleep(15);long none;Check(!s.TryRead(image,last,out none)&&none==0&&s.FramesProduced==before,"Old producer presentation never published");}
    using(Frame fail=new Frame{Fail=true}){f.Pending.Add(fail);Check(fail.Done.WaitOne(500),"No-frame source completed");long none;Check(!s.TryRead(image,last,out none)&&none==0,"No-frame result cannot relabel a timestamp");}
    using(Frame stopped=new Frame{Number=42}){stopped.Permit.Reset();f.Pending.Add(stopped);Check(stopped.Entered.WaitOne(500),"In-flight source entered");long before=s.FramesProduced;s.Stop();stopped.Permit.Set();Check(stopped.Done.WaitOne(500),"In-flight source released");Thread.Sleep(20);long none;Check(!s.TryRead(image,0,out none)&&none==0&&s.FramesProduced==before,"Stop vetoes a late producer completion");}
    Configure(s,area);Thread.Sleep(5);using(Frame focus=new Frame{Number=43}){f.Pending.Add(focus);Check(focus.Done.WaitOne(500),"Focus frame ready");Volatile.Write(ref f.Enabled,0);long none;Check(!s.TryRead(image,0,out none)&&none==0,"Focus identity vetoes delivery");Volatile.Write(ref f.Enabled,1);}
    Configure(s,area);Thread.Sleep(5);using(Frame changed=new Frame{Number=44}){changed.Permit.Reset();f.Pending.Add(changed);Check(changed.Entered.WaitOne(500),"Geometry source entered");Rectangle next=new Rectangle(100,200,321,181);Configure(s,next);changed.Permit.Set();Check(changed.Done.WaitOne(500),"Geometry source released");using(Bitmap moved=new Bitmap(321,181,PixelFormat.Format32bppRgb))using(Frame newFrame=new Frame{Number=45}){Thread.Sleep(5);f.Pending.Add(newFrame);Consume(s,moved,0);Pixels(moved,45);}}
    using(Bitmap heartbeatImage=new Bitmap(321,181,PixelFormat.Format32bppRgb))using(Frame heartbeat=new Frame{Number=46}){
     Rectangle next=new Rectangle(100,200,321,181);Configure(s,next);long produced=Interlocked.Read(ref s.FramesProduced);f.Pending.Add(heartbeat);Check(heartbeat.Done.WaitOne(500),"Heartbeat frame completed");
     Stopwatch published=Stopwatch.StartNew();while(Interlocked.Read(ref s.FramesProduced)==produced&&published.ElapsedMilliseconds<500)Thread.Sleep(1);Check(Interlocked.Read(ref s.FramesProduced)>produced,"Heartbeat test has unconsumed ready image");
     Thread.Sleep(400);long expired;Check(!s.TryRead(heartbeatImage,0,out expired)&&expired==0,"Heartbeat expiry rejects otherwise fresh unconsumed image");
    }
   }
   s.Dispose();Check(s.WorkerStopped,"Producer thread stopped");Check(f.Created==f.Disposed,"Every source disposed by its owner");
   File.WriteAllText(Path.Combine(args[0],"result.txt"),"PASS: "+checks+" async ownership checks; 41 entire RGB images validated including resize; duplicate, stale, no-frame, focus, stopped/in-flight, request-generation and heartbeat vetoes; source disposal and thread termination. Scripted source and presentation clock; real worker and buffers. No desktop capture, UI, network or input. Not native GPU latency evidence.");return 0;
  }catch(Exception ex){File.WriteAllText(Path.Combine(args[0],"failure.txt"),ex.ToString());return 1;}finally{s.Dispose();f.Pending.Dispose();}
 }
}
