//! Measures how long the overlay's own per-frame work takes, for keeping it cheap. Off unless the
//! ER_MOD_PROFILE environment variable is set, so normal play never even reads the clock for it.
//! Writes the average and worst frame to the log every few seconds.

use std::time::{Duration, Instant};

use crate::log;

const REPORT_EVERY: Duration = Duration::from_secs(5);

pub struct FrameCost {
    enabled: bool,
    frame: Duration,
    total: Duration,
    worst: Duration,
    frames: u32,
    since: Instant,
}

impl FrameCost {
    pub fn new() -> Self {
        Self {
            enabled: std::env::var_os("ER_MOD_PROFILE").is_some(),
            frame: Duration::ZERO,
            total: Duration::ZERO,
            worst: Duration::ZERO,
            frames: 0,
            since: Instant::now(),
        }
    }

    /// Starts timing one part of a frame; pass the result to `add`.
    pub fn start(&self) -> Option<Instant> {
        self.enabled.then(Instant::now)
    }

    pub fn add(&mut self, started: Option<Instant>) {
        if let Some(started) = started {
            self.frame += started.elapsed();
        }
    }

    /// Closes the frame and reports now and then.
    pub fn end_frame(&mut self) {
        if !self.enabled {
            return;
        }
        self.total += self.frame;
        self.worst = self.worst.max(self.frame);
        self.frames += 1;
        self.frame = Duration::ZERO;
        if self.since.elapsed() >= REPORT_EVERY && self.frames > 0 {
            log::write(&format!(
                "Overlay cost per frame: average {:.1} us, worst {:.1} us, over {} frames",
                self.total.as_secs_f64() * 1e6 / self.frames as f64,
                self.worst.as_secs_f64() * 1e6,
                self.frames
            ));
            self.total = Duration::ZERO;
            self.worst = Duration::ZERO;
            self.frames = 0;
            self.since = Instant::now();
        }
    }
}
