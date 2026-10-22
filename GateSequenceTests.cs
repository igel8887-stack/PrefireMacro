using System;
using System.Collections.Generic;
using System.Threading;

namespace PrefireMacro
{
    internal static class GateSequenceTests
    {
        static void Check(bool value, string message)
        { if (!value) throw new Exception("PrefireGate: " + message); }

        internal static void Run()
        {
            CheckBuildingCommit();
            CheckAimPolicy();
            CheckVisualPreflight();
            CheckWorkerConfirmation();
            CheckSlowFrameRecovery();
            CheckRestoredHudRecovery();
            CheckEditBuildSuppression();
            CheckExtraPlacements();
            PrefireGate fast = new PrefireGate(20);
            fast.Step(true, true, true, 0); fast.Step(true, false, true, 10);
            Check(!fast.Step(true, false, true, 29) && fast.Step(true, false, true, 30), "20 ms loss filter requests the first decision at its configured threshold");
            PrefireGate gate = new PrefireGate();
            Check(!gate.Step(true, false, false, 0) && !gate.Step(true, false, false, 500), "never fire when already absent at hold start");
            Check(!gate.Step(true, true, false, 520), "unstable target must not arm");
            Check(!gate.Step(true, false, false, 600) && !gate.Step(true, false, false, 700), "unstable target cannot trigger");
            gate.Step(true, true, true, 800);
            Check(!gate.Step(true, false, false, 813), "begin dropout");
            Check(!gate.Step(true, false, false, 865), "52 ms dropout must not trigger regardless of frame count");
            gate.Step(true, true, true, 870);
            Check(!gate.Step(true, false, false, 900), "new dropout has its own start");
            Check(!gate.Step(true, false, false, 959), "59 ms dropout");
            Check(gate.Step(true, false, false, 960) && gate.AwaitingResult, "60 ms dropout requests result");
            Check(!gate.Step(true, false, false, 1100), "pending decision cannot repeat");
            gate.ConfirmSent(1100);
            Check(!gate.AwaitingResult && !gate.Step(true, false, false, 2000), "permanent absence cannot cause repeated clicks");

            gate.Step(true, true, true, 2100);
            Check(!gate.Step(true, true, true, 2249), "reacquisition needs 150 ms");
            gate.Step(true, true, true, 2250);
            gate.Step(true, false, false, 2260);
            Check(gate.Step(true, false, false, 2321), "second real cycle during same hold");
            gate.ConfirmSent(2321);
            gate.Step(true, true, true, 2330);
            gate.Step(true, true, true, 2480);
            gate.Step(true, false, false, 2500);
            Check(!gate.Step(true, false, false, 2600), "150 ms reacquisition alone cannot bypass 300 ms cooldown");
            gate.Step(true, true, true, 2700);
            gate.Step(true, true, false, 2800);
            gate.Step(true, true, true, 2900);
            gate.Step(true, true, true, 3050);
            gate.Step(true, false, false, 3070);
            Check(gate.Step(true, false, false, 3200), "variable frame intervals and stable reacquisition");
            gate.Reject("Testfehler");
            Check(!gate.AwaitingResult && gate.Status.Contains("Testfehler") && gate.Status.Contains("loslassen"), "rejection must be visible");
            gate.Step(true, true, true, 3300);
            Check(!gate.Step(true, false, false, 3400), "rejected hold stays blocked");
            gate.Step(false, false, false, 3500);
            gate.Step(true, true, true, 3510);
            gate.Step(true, false, false, 3520);
            Check(gate.Step(true, false, false, 3580), "release clears rejection");
            gate.Reset();
            gate.ConfirmSent(3590);
            Check(!gate.AwaitingResult && !gate.Step(true, false, false, 4000), "focus reset ignores stale result");
            Check(!gate.Status.Contains("gesendet"), "stale callback does not claim sent input");
            gate.Step(true, true, true, 4100);
            gate.Step(true, true, false, 4110);
            gate.Step(true, false, false, 4120);
            Check(!gate.Step(true, false, false, 4300), "unstable candidate invalidates previous armed target");

            CheckSequenceFailures();
        }
        static void CheckExtraPlacements()
        {
            List<InputStep> steps = InputSequence.Plan(true, 5, 35, 10, new int[] { 70, 67 }, 15);
            Check(steps.Count == 14 && steps[6].Key == 70 && steps[10].Key == 67, "wall, stair, floor order");
            Check(InputSequence.Plan(false, 5, 35, 10, new int[] { 70, 67 }, 15).Count == 2, "extras disabled with building off");
            HashSet<int> owned = new HashSet<int>(); int placements = 0; SequenceResult completed = null;
            using (ManualResetEvent done = new ManualResetEvent(false))
            using (InputSequence sequence = new InputSequence(delegate(int key, bool down) { if (down) { owned.Add(key); if (key == 1) placements++; } else owned.Remove(key); return true; }, delegate { return false; }))
            {
                sequence.StartDetailed(steps, delegate { return true; }, delegate(SequenceResult result) { completed = result; done.Set(); });
                Check(done.WaitOne(2500) && completed.PlacementCount == 3 && placements == 4 && owned.Count == 0 && completed.Outcome == "Completed", "all separate placements delivered and released");
            }
            placements = 0;
            using (ManualResetEvent done = new ManualResetEvent(false))
            using (InputSequence sequence = new InputSequence(delegate(int key, bool down) { if (down) { owned.Add(key); if (key == 1) placements++; } else owned.Remove(key); return true; }, delegate { return false; }))
            {
                sequence.StartDetailed(steps, delegate { return true; }, delegate(SequenceResult result) { completed = result; done.Set(); }, null, null, null,
                    delegate(InputStep step, SequenceResult result, Func<bool> allowed) { if (step.Key == 70 && step.Down) throw new OperationCanceledException(); });
                Check(done.WaitOne(2500) && placements == 2 && completed.PlacementCount == 1 && owned.Count == 0 && completed.Outcome == "Cancelled", "focus cancellation before stair prevents extras and releases all inputs");
            }
        }
        static void CheckRestoredHudRecovery()
        {
            PrefireGate gate = new PrefireGate(20);
            gate.RequestFreshConfirmation(true, true, true, 100);
            Check(gate.RequestFreshConfirmation(true, false, true, 110), "initial target loss requests verification");
            // The worker vetoes the shot after seeing the HUD in a new frame.
            Check(gate.ResumeAfterRestoredHud(125, 135, true), "fresh restored HUD retains the previously armed target");
            Check(!gate.AwaitingResult, "a restored-HUD veto leaves no pending request");
            // No intervening visible frame is delivered by the UI timer.
            Check(gate.RequestFreshConfirmation(true, false, true, 140), "next loss does not require another visible UI-timer frame");
            long clock = 140; int checks = 0; string reason;
            Check(ShotVerifier.Confirm(140, 20, delegate { return clock; }, delegate(int ms) { clock += ms; }, delegate { return true; }, delegate { checks++; return true; }, out reason), "new loss still needs a distinct fresh missing frame after its own filter");
            Check(clock >= 160 && checks == 1, "recovery does not inherit elapsed time from the rejected loss");
            gate.ConfirmSent(clock);
            Check(!gate.ResumeAfterRestoredHud(150, 165, true) && !gate.RequestFreshConfirmation(true, false, true, 170), "late recovery callback cannot re-arm a completed shot");
            foreach (int age in new int[] { -1, 51, 300 })
            {
                gate.Reset(); gate.RequestFreshConfirmation(true, true, true, 100);
                gate.RequestFreshConfirmation(true, false, true, 110);
                Check(!gate.ResumeAfterRestoredHud(125, 125 + age, true), "invalid or stale recovery frame is rejected");
                Check(!gate.RequestFreshConfirmation(true, false, true, 500), "stale recovery cannot leave the target armed");
            }
            gate.Reset(); gate.RequestFreshConfirmation(true, true, true, 100); gate.RequestFreshConfirmation(true, false, true, 110);
            Check(!gate.ResumeAfterRestoredHud(125, 135, false) && !gate.RequestFreshConfirmation(true, false, true, 150), "focus/hold/enable guard blocks recovery");
            gate.Reset(); gate.RequestFreshConfirmation(true, true, true, 100); gate.RequestFreshConfirmation(true, false, true, 110); gate.Reset();
            Check(!gate.ResumeAfterRestoredHud(125, 135, true) && !gate.RequestFreshConfirmation(true, false, true, 150), "cancelled attempt ignores a late recovery callback");

            System.Drawing.Size client = new System.Drawing.Size(2560, 1440);
            System.Drawing.Rectangle area = HealthBarDetector.SearchArea(new System.Drawing.Rectangle(System.Drawing.Point.Empty, client));
            System.Drawing.Rectangle hud = new System.Drawing.Rectangle(1315, 604, 168, 18);
            HudAimGuard.RequestConfirmation(gate, true, true, true, hud, area, client, 600);
            Check(!HudAimGuard.RequestConfirmation(gate, true, false, false, hud, area, client, 1000), "expired tracker target cannot request a shot despite an old in-range rectangle");
            Check(!HudAimGuard.RequestConfirmation(gate, true, false, true, hud, area, client, 1010), "expired observation clears previous arming");
        }
        static void CheckAimPolicy()
        {
            System.Drawing.Size client = new System.Drawing.Size(2560, 1440);
            System.Drawing.Rectangle area = HealthBarDetector.SearchArea(new System.Drawing.Rectangle(System.Drawing.Point.Empty, client));
            Check(HudAimGuard.HudNearAim(new System.Drawing.Rectangle(1315, 604, 168, 18), area, client), "normal wall HUD remains eligible");
            foreach (System.Drawing.Rectangle panel in new System.Drawing.Rectangle[] {
                new System.Drawing.Rectangle(846, 1051, 168, 18), new System.Drawing.Rectangle(817, 1080, 168, 18),
                new System.Drawing.Rectangle(1294, 1150, 168, 18), new System.Drawing.Rectangle(1290, 1208, 168, 16) })
            {
                Check(HudAimGuard.HudNearAim(panel, area, client), "verified low HUD stays eligible when looking up at a ramp or close wall");
                PrefireGate raised = new PrefireGate(20);
                Check(!HudAimGuard.RequestConfirmation(raised, true, true, true, panel, area, client, 100), "visible elevated-view target arms without firing");
                Check(HudAimGuard.RequestConfirmation(raised, true, false, true, panel, area, client, 150), "loss of the same elevated-view target requests fresh verification");
            }
            Check(!HudAimGuard.HudNearAim(new System.Drawing.Rectangle(2300, 1200, 168, 18), area, client), "distant side panel remains ineligible");
            Check(!HudAimGuard.HudNearAim(System.Drawing.Rectangle.Empty, area, client), "no target cannot authorize a shot");
            PrefireGate gate = new PrefireGate(20);
            // Actual target coordinates from the V10 failure recording: these
            // small corrections previously reset arming on every mouse packet.
            foreach (System.Drawing.Rectangle hud in new System.Drawing.Rectangle[] {
                new System.Drawing.Rectangle(714, 601, 168, 18), new System.Drawing.Rectangle(717, 600, 168, 18) })
                Check(!HudAimGuard.RequestConfirmation(gate, true, true, true, hud, area, client, 161925), "visible near HUD arms without a stationary-mouse prerequisite");
            Check(HudAimGuard.RequestConfirmation(gate, true, false, true, new System.Drawing.Rectangle(717, 600, 168, 18), area, client, 161972), "first recorded loss requests fresh confirmation despite aim correction");
            Check(!HudAimGuard.RequestConfirmation(gate, true, false, true, new System.Drawing.Rectangle(717, 600, 168, 18), area, client, 162005), "pending loss does not repeat");
            gate.Reset();
            foreach (System.Drawing.Rectangle hud in new System.Drawing.Rectangle[] {
                new System.Drawing.Rectangle(757, 605, 168, 18), new System.Drawing.Rectangle(806, 591, 168, 18) })
                HudAimGuard.RequestConfirmation(gate, true, true, true, hud, area, client, 169200);
            Check(HudAimGuard.RequestConfirmation(gate, true, false, true, new System.Drawing.Rectangle(806, 591, 168, 18), area, client, 169265), "second recorded loss requests fresh confirmation after tracking motion");
            gate.Reset();
            HudAimGuard.RequestConfirmation(gate, true, true, true, new System.Drawing.Rectangle(1315, 604, 168, 18), area, client, 0);
            Check(!HudAimGuard.RequestConfirmation(gate, true, true, true, new System.Drawing.Rectangle(2300, 1080, 168, 18), area, client, 20) &&
                  !HudAimGuard.RequestConfirmation(gate, true, false, true, new System.Drawing.Rectangle(2300, 1080, 168, 18), area, client, 40), "moving the HUD far to the side clears arming and cannot fire on loss");
            Check(!HudAimGuard.RequestConfirmation(gate, true, false, false, System.Drawing.Rectangle.Empty, area, client, 60), "missing HUD at hold start never arms");
        }
        static void CheckBuildingCommit()
        {
            List<InputStep> plan = InputSequence.Plan(true, 5, 35, 35);
            Check(plan[3].Milliseconds - plan[2].Milliseconds == 35 && plan[5].Milliseconds - plan[3].Milliseconds == 80, "configurable selection delay and 80 ms placement hold");
            bool hold = true, focus = true; int calls = 0; SequenceResult result = null;
            using (ManualResetEvent done = new ManualResetEvent(false))
            using (InputSequence sequence = new InputSequence(delegate(int key, bool down) { calls++; if (key == 1 && down) hold = false; return true; }, delegate { return false; }))
            {
                sequence.StartDetailed(plan, delegate { return hold && focus; }, delegate(SequenceResult r) { result = r; done.Set(); }, null, null, delegate { return focus; });
                Check(done.WaitOne(2000) && result.ShotSent && result.BuildSent && result.PlaceSent && result.Outcome == "Completed" && calls == 6, "release after accepted shot completes exactly one build/place cycle");
                double gap = (result.PlaceDownTimestamp - result.BuildDownTimestamp) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
                Check(gap >= 34.5 && result.Steps.Split('|').Length == 6 && sequence.ShotCommitted, "actual accepted placement timing and all six steps are recorded");
            }
            hold = true; focus = true; calls = 0;
            using (ManualResetEvent done = new ManualResetEvent(false))
            using (InputSequence sequence = new InputSequence(delegate(int key, bool down) { calls++; if (key == 5 && down) focus = false; return true; }, delegate { return false; }))
            {
                sequence.StartDetailed(plan, delegate { return hold && focus; }, delegate(SequenceResult r) { result = r; done.Set(); }, null, null, delegate { return focus; });
                Check(done.WaitOne(2000) && result.ShotSent && result.BuildSent && !result.PlaceSent && result.Outcome == "Cancelled" && calls == 4, "focus loss after build selection prevents placement and releases owned build button");
            }
            hold = true; focus = true;
            using (ManualResetEvent built = new ManualResetEvent(false))
            using (ManualResetEvent done = new ManualResetEvent(false))
            using (InputSequence sequence = new InputSequence(delegate(int key, bool down) { if (key == 5 && down) built.Set(); return true; }, delegate { return false; }))
            {
                sequence.StartDetailed(InputSequence.Plan(true, 5, 35, 200), delegate { return true; }, delegate(SequenceResult r) { result = r; done.Set(); }, null, null, delegate { return true; });
                Check(built.WaitOne(2000), "emergency fixture reaches build selection"); sequence.Cancel();
                Check(done.WaitOne(2000) && !result.PlaceSent && result.Outcome == "Cancelled", "emergency cancellation still overrides a committed cycle");
            }
        }
        static void CheckWorkerConfirmation()
        {
            PrefireGate gate = new PrefireGate(20);
            Check(!gate.RequestFreshConfirmation(true, false, false, 0), "external verification cannot start without an acquired HUD");
            gate.RequestFreshConfirmation(true, true, true, 10);
            Check(gate.RequestFreshConfirmation(true, false, true, 20) && gate.AwaitingResult, "first loss requests verification without another UI timer tick");
            Check(!gate.RequestFreshConfirmation(true, false, true, 21), "only one verification can be pending");
            long clock = 110, freshAt = 0; int waits = 0, captures = 0; string reason;
            bool result = ShotVerifier.Confirm(100, 20, delegate { return clock; }, delegate(int ms) { waits++; clock += ms; }, delegate { return true; }, delegate { captures++; freshAt = clock; clock += 4; return true; }, out reason);
            Check(result && freshAt == 120 && waits == 10 && captures == 1, "first-frame processing counts toward debounce; a second frame starts only after 20 ms");
            clock = 135; waits = 0;
            result = ShotVerifier.Confirm(100, 20, delegate { return clock; }, delegate(int ms) { waits++; clock += ms; }, delegate { return true; }, delegate { clock += 4; return true; }, out reason);
            Check(result && waits == 0, "an already elapsed filter adds no second delay");
            clock = 110; captures = 0;
            result = ShotVerifier.Confirm(100, 20, delegate { return clock; }, delegate(int ms) { clock += ms; }, delegate { return clock < 115; }, delegate { captures++; return true; }, out reason);
            Check(!result && captures == 0, "release or focus loss during debounce prevents capture and input");
            clock = 120;
            result = ShotVerifier.Confirm(100, 20, delegate { return clock; }, delegate(int ms) { clock += ms; }, delegate { return true; }, delegate { clock += 51; return true; }, out reason);
            Check(!result, "a stale second-frame check cannot authorize input");
            clock = 120; captures = 0;
            result = ShotVerifier.Confirm(100, 20, delegate { return clock; }, delegate(int ms) { clock += ms; }, delegate { return true; }, delegate { clock += ++captures == 1 ? 62 : 4; return true; }, out reason);
            Check(result && captures == 2 && reason.Contains("Nachpruefungen=1"), "a 62 ms capture is discarded and a distinct fast frame can recover the shot");
            clock = 120; captures = 0;
            result = ShotVerifier.Confirm(100, 20, delegate { return clock; }, delegate(int ms) { clock += ms; }, delegate { return true; }, delegate { clock += ++captures == 1 ? 62 : 4; return captures == 1; }, out reason);
            Check(!result && captures == 2, "a returned wall in the retry still vetoes every input");
            int sent = 0; SequenceResult sequenceResult = null;
            using (ManualResetEvent done = new ManualResetEvent(false))
            using (InputSequence sequence = new InputSequence(delegate { sent++; return true; }, delegate { return false; }))
            {
                clock = 110;
                sequence.StartDetailed(InputSequence.Plan(false, 5, 35), delegate { return true; }, delegate(SequenceResult value) { sequenceResult = value; done.Set(); }, delegate
                {
                    string details;
                    return ShotVerifier.Confirm(100, 20, delegate { return clock; }, delegate(int ms) { clock += ms; }, delegate { return true; }, delegate { return false; }, out details);
                });
                Check(done.WaitOne(2000) && sent == 0 && !sequenceResult.ShotSent, "a restored HUD in the second frame sends no mouse input");
            }
            gate.Reset();
            Check(!gate.RequestFreshConfirmation(true, false, false, 30), "after a transient veto, missing frames alone cannot rearm");
            gate.RequestFreshConfirmation(true, true, true, 40);
            Check(gate.RequestFreshConfirmation(true, false, true, 50), "a new stable HUD can start another verified cycle without requiring release");
        }
        static void CheckSlowFrameRecovery()
        {
            PrefireGate gate = new PrefireGate(20);
            gate.RequestFreshConfirmation(true, true, true, 10);
            Check(gate.RequestFreshConfirmation(true, false, true, 20), "seed pending verification");
            gate.RetryFresh(150);
            Check(!gate.AwaitingResult && !gate.RequestFreshConfirmation(true, false, true, 179), "slow capture recovery has a short backoff");
            Check(gate.RequestFreshConfirmation(true, false, true, 180), "same held target can request a new verification without releasing");
            gate.RetryFresh(290);
            Check(!gate.RequestFreshConfirmation(true, false, true, 321) && !gate.RequestFreshConfirmation(true, false, false, 500), "retry target expires rather than firing from an old disappearance");
            gate.RequestFreshConfirmation(true, true, true, 510);
            Check(gate.RequestFreshConfirmation(true, false, true, 530), "a new visible target rearms after retry expiry");
            gate.RetryFresh(550);
            Check(!gate.RequestFreshConfirmation(false, false, true, 600) && !gate.RequestFreshConfirmation(true, false, true, 650), "release clears delayed retry authorization");
        }
        static void CheckEditBuildSuppression()
        {
            foreach (bool suppress in new bool[] { false, true })
            {
                int down = 0, up = 0; SequenceResult result = null;
                using (ManualResetEvent done = new ManualResetEvent(false))
                using (InputSequence sequence = new InputSequence(delegate(int key, bool held) { if (held) down++; else up++; return true; }, delegate { return false; }))
                {
                    sequence.StartDetailed(InputSequence.Plan(true, 5, 35, 15), delegate { return true; }, delegate(SequenceResult value) { result = value; done.Set(); }, null, null, delegate { return true; }, delegate(InputStep step, SequenceResult current, Func<bool> allowed)
                    { if (suppress && current.ShotSent && !current.BuildSent && step.Down && step.Key == 5) throw new BuildSuppressedException("Edit-Schutz fixture"); });
                    Check(done.WaitOne(2000) && result.ShotSent && result.BuildSuppressed == suppress && result.BuildSent == !suppress && result.PlaceSent == !suppress && down == up, "optional edit guard preserves the shot, omits build and placement, and releases every owned input");
                    Check(result.Outcome == (suppress ? "ShotOnly" : "Completed") && down == (suppress ? 1 : 3), "guard is recorded as shot-only, not a failed shot");
                }
            }
        }
        static void CheckVisualPreflight()
        {
            int sent = 0, checkedFrames = 0, firstCallbacks = 0;
            SequenceResult rejected = null;
            using (ManualResetEvent done = new ManualResetEvent(false))
            using (InputSequence sequence = new InputSequence(delegate { sent++; return true; }, delegate { return false; }))
            {
                sequence.StartDetailed(InputSequence.Plan(false, 5, 70), delegate { return true; }, delegate(SequenceResult result) { rejected = result; done.Set(); }, delegate { checkedFrames++; return false; }, delegate { firstCallbacks++; });
                Check(done.WaitOne(2000), "visual preflight completes");
                Check(checkedFrames == 1 && sent == 0 && firstCallbacks == 0 && !rejected.ShotSent, "restored HUD rejects the first input before any click or snapshot callback");
            }
            using (ManualResetEvent done = new ManualResetEvent(false))
            using (InputSequence sequence = new InputSequence(delegate { sent++; return true; }, delegate { return false; }))
            {
                sequence.StartDetailed(InputSequence.Plan(true, 5, 35), delegate { return true; }, delegate { done.Set(); }, delegate { checkedFrames++; return true; }, delegate { firstCallbacks++; });
                Check(done.WaitOne(2000) && firstCallbacks == 1 && checkedFrames == 2 && sent == 6, "accepted first down notifies once; the placement click does not repeat preflight or snapshot");
            }
        }
        static SequenceResult RunMock(Func<int, bool, bool> sender, Func<bool> allowed)
        {
            SequenceResult result = null;
            using (ManualResetEvent done = new ManualResetEvent(false))
            using (InputSequence runner = new InputSequence(sender, delegate { return false; }))
            {
                List<InputStep> plan = new List<InputStep> { new InputStep(0, 1, true), new InputStep(0, 1, false) };
                Check(runner.StartDetailed(plan, allowed, delegate(SequenceResult value) { result = value; done.Set(); }), "mock starts");
                Check(done.WaitOne(2000), "mock completion");
            }
            return result;
        }
        static void CheckSequenceFailures()
        {
            SequenceResult before = RunMock(delegate { return false; }, delegate { return true; });
            Check(!before.ShotSent && before.Outcome == "Failed", "rejected first down must not count");
            Check(before.FirstDownTimestamp == 0, "no first-down timestamp for a rejected input");
            int calls = 0;
            SequenceResult after = RunMock(delegate(int key, bool down) { calls++; return calls != 2; }, delegate { return true; });
            Check(after.ShotSent && after.Outcome == "Failed" && calls >= 3, "accepted first down remains counted after later failure and cleanup");
            Check(after.FirstDownTimestamp > 0, "accepted first-down timestamp survives a later error");
            int sent = 0;
            SequenceResult cancelledBefore = RunMock(delegate { sent++; return true; }, delegate { return false; });
            Check(!cancelledBefore.ShotSent && sent == 0 && cancelledBefore.Outcome == "Cancelled", "focus gate blocks first down");
            bool allow = true, downHeld = false;
            SequenceResult cancelledAfter = RunMock(delegate(int key, bool down) { downHeld = down; if (down) allow = false; return true; }, delegate { return allow; });
            Check(cancelledAfter.ShotSent && !downHeld && cancelledAfter.Outcome == "Cancelled", "focus loss after first down preserves sent result and releases input");
        }
    }
}
