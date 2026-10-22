using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.Threading;
using System.Diagnostics;
using System.IO;
using PrefireMacro;

class AsyncPipelineTests
{
 static int checks;
 static void Check(bool ok,string reason){if(!ok)throw new Exception(reason);checks++;}
 static Rectangle client=new Rectangle(0,0,2560,1440),area=new Rectangle(128,216,320,180);
 sealed class FakeSource:AsyncDesktopSource.IFrameSource {
  internal readonly ManualResetEvent Ready=new ManualResetEvent(false);
  internal int sent;
  public long Timestamp{get;private set;} public string State{get{return "Scripted";}}
  public bool Capture(Rectangle a,Bitmap target,long after){if(Interlocked.Exchange(ref sent,1)!=0){Thread.Sleep(1);return false;}using(Graphics g=Graphics.FromImage(target))g.Clear(Color.FromArgb(31,47,71));Timestamp=1005;Ready.Set();return true;}
  public void Dispose(){Ready.Dispose();}
 }
 sealed class Shared:IRecentHudFrames {
  internal long At;internal int Calls;internal bool Available=true;
  public bool WaitingForNewFrames{get{return Available;}}
  public bool TryRead(IntPtr w,uint p,Rectangle c,Rectangle a,Bitmap target,long after,out long at){Calls++;at=At;if(!Available)return false;using(Graphics g=Graphics.FromImage(target))g.Clear(Color.FromArgb(40,50,60));return true;}
 }
 static void ConfirmTests(){
  Check(HealthBarCapture.WaitForTrackingFrame(true,1000,1015),"Recent tracking image permits a new timer tick");
  Check(!HealthBarCapture.WaitForTrackingFrame(true,1000,1016),"Tracking grace bounded to 16ms");
  Check(!HealthBarCapture.WaitForTrackingFrame(false,1000,1005),"Unavailable GPU never delays compatibility tracking");
  Check(!HealthBarCapture.WaitForTrackingFrame(true,0,1005)&&!HealthBarCapture.WaitForTrackingFrame(true,1006,1005),"Tracking grace requires a valid existing image timestamp");
  foreach(int availableAfter in new int[]{0,1,11,12,13}){
   long now=1000;int waits=0;
   bool got=ShotVerifier.TryRecentFrame(delegate{return now>=1000+availableAfter;},delegate{return true;},delegate{return now;},delegate(int n){now+=n;waits++;},true);
   Check(got==(availableAfter<=12)&&waits<=12,"Bounded next-frame wait: "+availableAfter);
  }
  {
   long now=1000;int waits=0;
   Check(!ShotVerifier.TryRecentFrame(delegate{return false;},delegate{return true;},delegate{return now;},delegate(int n){now+=n;waits++;},false)&&waits==0,"Unavailable GPU does not add waiting to GDI fallback");
   Check(!ShotVerifier.TryRecentFrame(delegate{return false;},delegate{return now<1003;},delegate{return now;},delegate(int n){now+=n;},true)&&now==1003,"Hold or focus change interrupts the next-frame wait");
  }
  foreach(long at in new long[]{0,1000,1019,1021}){
   long now=1020;int captures=0;string why;
   bool accepted=ShotVerifier.Confirm(1000,20,delegate{return now;},delegate(int n){now+=n;},delegate{return true;},delegate{captures++;return true;},out why,delegate{return at;});
   Check(!accepted&&captures==2,"Duplicate, pre-filter or future evidence rejected: "+at);
  }
  foreach(long age in new long[]{0,12,50,51}){
   long now=1020+age;string why;
   bool accepted=ShotVerifier.Confirm(1000,20,delegate{return now;},delegate(int n){now+=n;},delegate{return true;},delegate{return true;},out why,delegate{return 1020;});
   Check(accepted==(age<=50),"Post-classification image-age boundary: "+age);
  }
  {
   long now=1000;string why;int waits=0,captures=0;
   bool accepted=ShotVerifier.Confirm(1000,20,delegate{return now;},delegate(int n){now+=n;waits++;},delegate{return true;},delegate{captures++;return true;},out why,delegate{return now;});
   Check(accepted&&waits==20&&captures==1,"Filter retained before fresh evidence");
  }
  {
   long now=1020;int calls=0;long at=0;string why;
   bool accepted=ShotVerifier.Confirm(1000,20,delegate{return now;},delegate(int n){now+=n;},delegate{return true;},delegate{calls++;at=now-12;now+=40;return true;},out why,delegate{return at;});
   Check(!accepted&&calls==2,"Processing under 50ms cannot validate an image older than 50ms");
  }
  {
   long now=1020;bool allowed=true;string why;
   Check(!ShotVerifier.Confirm(1000,20,delegate{return now;},delegate(int n){now+=n;},delegate{return allowed;},delegate{allowed=false;return true;},out why,delegate{return now;}),"Cancellation after image classification vetoes confirmation");
   allowed=true;
   Check(!ShotVerifier.Confirm(1000,20,delegate{return now;},delegate(int n){now+=n;},delegate{return allowed;},delegate{return false;},out why,delegate{return now;}),"Restored HUD vetoes confirmation");
  }
 }
 static void SharedTests(){
  using(HealthBarCapture capture=new HealthBarCapture()){
   Shared fake=new Shared();long now=ShotVerifier.ClockMs();fake.At=now-51;
   Check(!capture.TrySharedFrame(fake,new IntPtr(1),42,client,area,0),"Provider stale image rejected independently");
   fake.At=ShotVerifier.ClockMs()+1000;
   Check(!capture.TrySharedFrame(fake,new IntPtr(1),42,client,area,0),"Provider future image rejected independently");
   fake.At=ShotVerifier.ClockMs();
   Check(!capture.TrySharedFrame(fake,new IntPtr(1),42,client,area,fake.At),"Provider duplicate presentation rejected independently");
   fake.At=ShotVerifier.ClockMs();
   Check(capture.TrySharedFrame(fake,new IntPtr(1),42,client,area,fake.At-1),"Fresh provider image accepted");
   Check(capture.CapturedAtMs==fake.At&&capture.Frame.PixelFormat==PixelFormat.Format32bppRgb&&capture.LastArea==area&&capture.LastClientSize==client.Size,"Provider timestamp and geometry retained without relabelling");
   fake.Available=false;
   Check(!capture.TrySharedFrame(fake,new IntPtr(1),42,client,area,0),"Unavailable provider asks caller for compatibility capture");
   Check(!capture.TrySharedFrame(null,new IntPtr(1),42,client,area,0),"Absent provider asks caller for compatibility capture");
  }
 }
 static void OwnershipTests(int expiryMode){
  FakeSource native=new FakeSource();int consumer=Thread.CurrentThread.ManagedThreadId,allowedReads=0;
  Func<AsyncDesktopSource.Request,bool> allowed=delegate(AsyncDesktopSource.Request r){if(Thread.CurrentThread.ManagedThreadId==consumer){allowedReads++;if(expiryMode==2&&allowedReads>=2)return false;}return true;};
  Func<long> clock=delegate {return Thread.CurrentThread.ManagedThreadId==consumer&&expiryMode==1&&allowedReads>=2?1020:1005;};
  // Configure starts at 1005, then the producer supplies a strictly later presentation.
  bool configured=false;Func<long> requestClock=delegate{return !configured?1000:clock();};
  using(AsyncDesktopSource source=new AsyncDesktopSource(allowed,delegate(Rectangle r){return native;},requestClock))using(Bitmap image=new Bitmap(area.Width,area.Height,PixelFormat.Format32bppRgb)){
   source.Configure(new IntPtr(123),42,client,area);configured=true;
   Stopwatch wait=Stopwatch.StartNew();while(Interlocked.Read(ref source.FramesProduced)==0&&wait.ElapsedMilliseconds<500)Thread.Sleep(1);
   Check(source.FramesProduced==1,"Scripted frame published");long at;
   Check(!source.TryRead(new IntPtr(124),42,client,area,image,0,out at),"Wrong window rejected");
   Check(!source.TryRead(new IntPtr(123),43,client,area,image,0,out at),"Wrong process rejected");
   Check(!source.TryRead(new IntPtr(123),42,new Rectangle(1,0,2560,1440),area,image,0,out at),"Moved client rejected");
   Check(!source.TryRead(new IntPtr(123),42,client,new Rectangle(129,216,320,180),image,0,out at),"Same-size different capture area rejected");
   Check(allowedReads==0,"Identity veto precedes source and bitmap access");
   bool copied=source.TryRead(new IntPtr(123),42,client,area,image,0,out at);
   Check(copied==(expiryMode==0),"Post-copy freshness and focus checks: "+expiryMode);
   if(copied){Check(at==1005,"Actual presentation time returned");Check(!source.TryRead(new IntPtr(123),42,client,area,image,0,out at),"Second consumer cannot reuse presentation");Check(image.GetPixel(0,0).ToArgb()==Color.FromArgb(31,47,71).ToArgb(),"Private RGB image content retained");}
   else Check(at==0,"Rejected copy does not return a usable timestamp");
  }
 }
 static int Main(string[] args){try{ConfirmTests();SharedTests();OwnershipTests(0);OwnershipTests(1);OwnershipTests(2);File.WriteAllText(args[0],"PASS: "+checks+" pipeline checks. Full requested identity; post-copy focus/age veto; duplicate and future images; filter boundary; evidence age after classification; restored HUD and cancellation. No desktop capture or input.");return 0;}catch(Exception e){File.WriteAllText(args[0],"FAIL: "+e);return 1;}}
}
