//! The preview's seek pacing, apart from mpv. One exact seek goes at a time; later ones wait and collapse into the
//! latest, which goes once mpv reports the seek finished (`restarted`). mpv drops an unfinished exact seek for a
//! newer one, so seeking at the pointer's pace would show nothing until the pointer stopped (an exact seek in a
//! long GOP can take hundreds of ms). A seek mpv never reports finished is checked every `CHECK_INTERVAL`: while
//! mpv is still seeking it keeps waiting, however long that takes; otherwise the next one goes. Thread-safe: seeks
//! come from the UI thread, restarts from mpv's event thread and checks from a timer; `dispose` waits for calls
//! into mpv under way on those threads, so none reaches a destroyed mpv.

use crate::model::Time;
use std::sync::{Condvar, Mutex};
use std::time::{Duration, Instant};

/// How often to check on a seek mpv hasn't reported finished: one that failed or was lost lets the next go.
pub const CHECK_INTERVAL: Duration = Duration::from_secs(1);
/// For this long after a seek is sent, position reports may still echo the old position.
pub const ECHO_WINDOW: Duration = Duration::from_millis(250);
/// The longest `dispose` waits for a call into mpv to return.
const DISPOSE_WAIT: Duration = Duration::from_secs(5);

pub type Send = Box<dyn Fn(Time) + std::marker::Send + Sync>;
pub type IsSeeking = Box<dyn Fn() -> bool + std::marker::Send + Sync>;
pub type ArmCheck = Box<dyn Fn(bool) + std::marker::Send + Sync>;
pub type Now = Box<dyn Fn() -> Instant + std::marker::Send + Sync>;

#[derive(Default)]
struct State {
    pending: Option<Time>,
    /// A seek is under way (until mpv reports it finished or stops seeking); `target` is where it goes.
    seeking: bool,
    target: Time,
    last_sent_at: Option<Instant>,
    disposed: bool,
    /// Calls into mpv under way (send or is_seeking), which dispose waits for.
    in_flight: usize,
}

pub struct SeekPacer {
    send: Send,
    is_seeking: IsSeeking,
    arm_check: ArmCheck,
    now: Now,
    gate: Mutex<State>,
    idle: Condvar,
}

impl SeekPacer {
    pub fn new(send: Send, is_seeking: IsSeeking, arm_check: ArmCheck, now: Option<Now>) -> SeekPacer {
        SeekPacer {
            send,
            is_seeking,
            arm_check,
            now: now.unwrap_or_else(|| Box::new(Instant::now)),
            gate: Mutex::new(State::default()),
            idle: Condvar::new(),
        }
    }

    /// Seeks to `time` now, or as soon as the seek under way has finished.
    pub fn seek(&self, time: Time) {
        {
            let mut s = self.gate.lock().unwrap();
            if s.seeking {
                // The pointer moving along the time it's already at (up and down) costs no second decode.
                s.pending = if time == s.target { None } else { Some(time) };
                return;
            }
            s.pending = Some(time);
            s.seeking = true;
        }
        self.flush();
    }

    /// mpv finished a seek (or a load): the latest waiting seek goes now.
    pub fn restarted(&self) {
        if !self.gate.lock().unwrap().seeking {
            return;
        }
        self.flush();
    }

    /// The armed check: a seek still under way keeps the next one waiting; one mpv isn't working on lets it go.
    pub fn check(&self) {
        {
            let mut s = self.gate.lock().unwrap();
            if s.disposed || !s.seeking {
                return;
            }
            s.in_flight += 1;
        }
        let still = (self.is_seeking)();
        self.exit();
        if still {
            let s = self.gate.lock().unwrap();
            if !s.disposed && s.seeking {
                (self.arm_check)(true);
            }
            return;
        }
        self.flush();
    }

    /// The file changed or went away: a seek under way and the waiting one are dropped, the next goes at once.
    pub fn reset(&self) {
        let mut s = self.gate.lock().unwrap();
        s.pending = None;
        s.seeking = false;
    }

    /// A position report now may be an echo of the old position: a seek is waiting or was just sent.
    pub fn may_echo(&self) -> bool {
        let s = self.gate.lock().unwrap();
        s.pending.is_some() || s.last_sent_at.is_some_and(|t| (self.now)().saturating_duration_since(t) <= ECHO_WINDOW)
    }

    /// Position reports are to be trusted again at once (frame stepping reports where it landed).
    pub fn forget_last_seek(&self) {
        self.gate.lock().unwrap().last_sent_at = None;
    }

    /// Nothing more is sent or armed, and calls into mpv under way have returned.
    pub fn dispose(&self) {
        let mut s = self.gate.lock().unwrap();
        s.disposed = true;
        let until = Instant::now() + DISPOSE_WAIT;
        while s.in_flight > 0 {
            let left = until.saturating_duration_since(Instant::now());
            if left.is_zero() {
                break;
            }
            s = self.idle.wait_timeout(s, left).unwrap().0;
        }
    }

    fn exit(&self) {
        let mut s = self.gate.lock().unwrap();
        s.in_flight -= 1;
        if s.in_flight == 0 {
            self.idle.notify_all();
        }
    }

    fn flush(&self) {
        let next;
        {
            let mut s = self.gate.lock().unwrap();
            if s.disposed {
                return;
            }
            next = s.pending.take();
            s.seeking = next.is_some();
            if let Some(target) = next {
                s.target = target;
                s.last_sent_at = Some((self.now)());
                s.in_flight += 1;
            }
            (self.arm_check)(s.seeking);
        }
        if let Some(t) = next {
            (self.send)(t);
            self.exit();
        }
    }
}
