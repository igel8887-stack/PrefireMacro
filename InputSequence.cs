using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

namespace PrefireMacro
{
    internal sealed class BuildSuppressedException : Exception
    { internal BuildSuppressedException(string reason) : base(reason) { } }
    internal sealed class InputStep
    {
        public int Milliseconds, Key; public bool Down;
        public InputStep(int ms, int key, bool down) { Milliseconds = ms; Key = key; Down = down; }
    }
    internal sealed class SequenceResult
    {
        public bool ShotSent;
        public long FirstDownTimestamp;
        public long BuildDownTimestamp, PlaceDownTimestamp;
        public bool BuildSent, PlaceSent;
        public int PlacementCount;
        public bool BuildSuppressed;
        public string Steps = "";
        public string Message;
        public string Outcome;
    }
    internal sealed class InputSequence : IDisposable
    {
        readonly ManualResetEvent cancel = new ManualResetEvent(false);
        readonly Func<int, bool, bool> send;
        readonly Func<int, bool> held;
        Thread worker;
        int running, committed;
        [DllImport("winmm.dll")] static extern uint timeBeginPeriod(uint period);
        [DllImport("winmm.dll")] static extern uint timeEndPeriod(uint period);
        public bool Running { get { return Interlocked.CompareExchange(ref running, 0, 0) != 0; } }
        public bool ShotCommitted { get { return Interlocked.CompareExchange(ref committed, 0, 0) != 0; } }
        public InputSequence() : this(Native.SetButton, Native.Held) { }
        public InputSequence(Func<int, bool, bool> sender, Func<int, bool> isHeld) { send = sender; held = isHeld; }
        public static List<InputStep> Plan(bool build, int key, int delay)
        { return Plan(build, key, delay, 10); }
        public static List<InputStep> Plan(bool build, int key, int delay, int placeDelay)
        { return Plan(build, key, delay, placeDelay, new int[0], 100); }
        public static List<InputStep> Plan(bool build, int key, int delay, int placeDelay, int[] additionalKeys, int extraDelay)
        {
            List<InputStep> steps = new List<InputStep>();
            steps.Add(new InputStep(0, 1, true)); steps.Add(new InputStep(30, 1, false));
            if (build)
            {
                int at = Math.Max(35, delay);
                steps.Add(new InputStep(at, key, true));
                int placeAt = at + Math.Max(10, placeDelay);
                steps.Add(new InputStep(placeAt, 1, true));
                steps.Add(new InputStep(placeAt + 20, key, false));
                steps.Add(new InputStep(placeAt + 80, 1, false));
                int end = placeAt + 80;
                foreach (int extra in additionalKeys)
                {
                    int selectAt = end + Math.Max(15, extraDelay);
                    int clickAt = selectAt + Math.Max(10, placeDelay);
                    steps.Add(new InputStep(selectAt, extra, true));
                    steps.Add(new InputStep(clickAt, 1, true));
                    steps.Add(new InputStep(clickAt + 20, extra, false));
                    steps.Add(new InputStep(clickAt + 80, 1, false));
                    end = clickAt + 80;
                }
            }
            return steps;
        }
        public bool Start(List<InputStep> steps, Func<bool> allowed, Action<string> completed)
        {
            return StartDetailed(steps, allowed, delegate(SequenceResult result) { if (completed != null) completed(result.Message); });
        }
        public bool StartDetailed(List<InputStep> steps, Func<bool> allowed, Action<SequenceResult> completed)
        { return StartDetailed(steps, allowed, completed, null); }
        public bool StartDetailed(List<InputStep> steps, Func<bool> allowed, Action<SequenceResult> completed, Func<bool> beforeFirstDown, Action firstDownSent = null, Func<bool> afterShotAllowed = null, Action<InputStep, SequenceResult, Func<bool>> beforeStep = null)
        {
            if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return false;
            cancel.Reset(); Interlocked.Exchange(ref committed, 0);
            worker = new Thread(delegate()
            {
                HashSet<int> owned = new HashSet<int>();
                SequenceResult result = new SequenceResult { Message = "Eingabefolge an Windows gesendet.", Outcome = "Completed" };
                Stopwatch watch = new Stopwatch(); int previous = 0;
                bool timerRequested = timeBeginPeriod(1) == 0;
                Func<bool> canContinue = delegate { return result.ShotSent && afterShotAllowed != null ? afterShotAllowed() : allowed(); };
                try
                {
                    foreach (InputStep step in steps)
                    {
                        int gap = step.Milliseconds - previous;
                        while (watch.Elapsed.TotalMilliseconds < gap)
                        {
                            if (cancel.WaitOne(1) || !canContinue()) throw new OperationCanceledException();
                        }
                        if (cancel.WaitOne(0) || !canContinue()) throw new OperationCanceledException();
                        if (step.Key == 1 && step.Down && !result.ShotSent && beforeFirstDown != null)
                        {
                            if (!beforeFirstDown()) throw new InvalidOperationException("Aktuelle Bildprüfung verwirft den Schussversuch.");
                            if (cancel.WaitOne(0) || !allowed()) throw new OperationCanceledException();
                        }
                        if (beforeStep != null) beforeStep(step, result, delegate { return !cancel.WaitOne(0) && canContinue(); });
                        if (cancel.WaitOne(0) || !canContinue()) throw new OperationCanceledException();
                        if (step.Down && held(step.Key)) throw new InvalidOperationException("Eine benötigte Taste ist bereits gehalten. Folge abgebrochen.");
                        if (!send(step.Key, step.Down)) throw new InvalidOperationException("Windows hat eine Eingabe nicht angenommen.");
                        long acceptedAt = Stopwatch.GetTimestamp();
                        result.Steps += (result.Steps.Length == 0 ? "" : "|") + step.Key + (step.Down ? "D" : "U") + "@" + acceptedAt;
                        if (step.Key == 1 && step.Down && !result.ShotSent)
                        { result.ShotSent = true; result.FirstDownTimestamp = acceptedAt; Interlocked.Exchange(ref committed, 1); if (firstDownSent != null) try { firstDownSent(); } catch { } }
                        else if (step.Down && step.Key == 1) { if (!result.PlaceSent) result.PlaceDownTimestamp = acceptedAt; result.PlaceSent = true; result.PlacementCount++; }
                        else if (step.Down && !result.BuildSent) { result.BuildSent = true; result.BuildDownTimestamp = acceptedAt; }
                        if (step.Down) owned.Add(step.Key); else owned.Remove(step.Key);
                        previous = step.Milliseconds; watch.Restart();
                    }
                }
                catch (BuildSuppressedException ex) { result.Message = ex.Message; result.Outcome = "ShotOnly"; result.BuildSuppressed = true; }
                catch (OperationCanceledException) { result.Message = "Eingabefolge abgebrochen."; result.Outcome = "Cancelled"; }
                catch (Exception ex) { result.Message = ex.Message; result.Outcome = "Failed"; }
                finally
                {
                    foreach (int key in owned)
                    {
                        bool released = false;
                        for (int attempt = 0; attempt < 3 && !released; attempt++)
                            try { released = send(key, false); } catch { }
                        if (!released) { result.Message += " Eine eigene Taste konnte nicht freigegeben werden."; result.Outcome = "Failed"; }
                    }
                    if (timerRequested) timeEndPeriod(1);
                    Interlocked.Exchange(ref running, 0);
                    if (completed != null) completed(result);
                }
            });
            worker.IsBackground = true; worker.Name = "Prefire input sequence"; worker.Start(); return true;
        }
        public void Cancel() { cancel.Set(); }
        public void Dispose() { Cancel(); if (worker != null && worker.IsAlive) worker.Join(400); if (!Running) cancel.Dispose(); }
    }
}
