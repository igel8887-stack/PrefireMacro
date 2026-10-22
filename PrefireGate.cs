using System;

namespace PrefireMacro
{
    // Pure state machine: observing a missing HUD element never means an input was sent.
    public sealed class PrefireGate
    {
        readonly int lossDelayMs;
        bool armed, pending, rejected, waitingForNewTarget;
        long lossSince = -1, stableSince = -1, sentAt = -1, retryAt = -1;
        string status;

        public PrefireGate(int lossDelayMs = 60)
        {
            if (lossDelayMs < 1) throw new ArgumentOutOfRangeException("lossDelayMs");
            this.lossDelayMs = lossDelayMs;
            Reset();
        }
        public string Status { get { return status; } }
        public bool AwaitingResult { get { return pending; } }
        public void Reset()
        {
            armed = pending = rejected = waitingForNewTarget = false;
            lossSince = stableSince = sentAt = retryAt = -1;
            status = "Warte auf einen stabilen Lebensbalken.";
        }
        public bool Step(bool held, bool visible, bool stable, long now)
        { return Step(held, visible, stable, now, false); }
        // The input worker must confirm a second, fresh missing frame after the filter.
        // This requests verification, never an unverified input.
        internal bool RequestFreshConfirmation(bool held, bool visible, bool stable, long now)
        { return Step(held, visible, stable, now, true); }
        bool Step(bool held, bool visible, bool stable, long now, bool externalConfirmation)
        {
            if (!held) { Reset(); return false; }
            if (rejected || pending) return false;
            if (retryAt >= 0)
            {
                if (lossSince < 0 || now - lossSince > 300) { Reset(); return false; }
                if (now < retryAt) return false;
                retryAt = -1;
            }
            if (visible)
            {
                lossSince = -1;
                if (!stable)
                {
                    armed = false;
                    stableSince = -1;
                    status = "Lebensbalken wird erneut bestätigt.";
                    return false;
                }
                if (waitingForNewTarget)
                {
                    if (stableSince < 0) stableSince = now;
                    if (now - stableSince < 150 || now - sentAt < 300)
                    {
                        status = "Eingabe gesendet – warte auf stabilen neuen Balken.";
                        return false;
                    }
                    waitingForNewTarget = false;
                }
                armed = true;
                status = "Stabiler Lebensbalken erkannt – bereit.";
                return false;
            }
            stableSince = -1;
            if (!armed)
            {
                status = waitingForNewTarget ? "Eingabe gesendet – warte auf neuen Balken." : "Warte auf einen stabilen Lebensbalken.";
                return false;
            }
            if (lossSince < 0) lossSince = now;
            if (!externalConfirmation && now - lossSince < lossDelayMs)
            {
                status = "Kurzen Balkenausfall prüfen …";
                return false;
            }
            pending = true;
            status = externalConfirmation ? "Balkenverlust erkannt – frisches Bild nach Verlustfilter prüfen." : "Balkenverlust bestätigt – warte auf Eingabeergebnis.";
            return true;
        }
        public void ConfirmSent(long now)
        {
            // Ignore a callback from a sequence invalidated by Reset/focus loss.
            if (!pending) return;
            pending = false;
            armed = false;
            waitingForNewTarget = true;
            lossSince = stableSince = -1;
            sentAt = now;
            status = "Eingabe an Windows gesendet – warte auf neuen Balken.";
        }
        public void Reject(string reason)
        {
            if (!pending) return;
            pending = false;
            armed = false;
            rejected = true;
            status = "Keine Schusseingabe gesendet: " + (String.IsNullOrEmpty(reason) ? "Folge abgebrochen." : reason) + " Haltetaste loslassen.";
        }
        internal bool RetryFresh(long now)
        {
            if (!pending) return false;
            pending = false;
            if (lossSince < 0 || now - lossSince > 270) { Reset(); return false; }
            retryAt = now + 30;
            status = "Bildprüfung verzögert – neues Bild wird geprüft.";
            return true;
        }
        internal bool ResumeAfterRestoredHud(long capturedAt, long now, bool allowed)
        {
            // A stale callback must not erase a sent-shot cooldown or re-arm it.
            if (!pending) return false;
            if (!armed || !allowed || capturedAt <= 0 || now < capturedAt || now - capturedAt > 50)
            { Reset(); return false; }
            pending = false;
            lossSince = stableSince = retryAt = -1;
            status = "Balken wieder sichtbar – nächster Verlust wird erneut geprüft.";
            return true;
        }
    }
}
