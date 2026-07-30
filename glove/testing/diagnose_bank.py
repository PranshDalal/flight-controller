#!/usr/bin/env python3
"""
Diagnoses the "pitch up -> plane auto-banks on takeoff" symptom by analyzing
real hub pitch/roll movement from the glove.

Could be one of three things: mechanical wrist coupling (pitching the wrist
naturally drags in some roll - flight_controller.ino has an unmeasured
PITCH_TO_ROLL_COUPLING constant for this), the accelerometer trust gate
getting violated during a fast pull, or residual gyro drift. This script
checks for all three instead of assuming which one it is - it finds "pitch
events" (deliberate tilt-away-and-back motions) and measures how much roll
rides along with each one, how often the accelerometer falls outside its
trust gate during those events vs. at rest, and roll drift during calm
stretches.

Two ways to get data in:

  1. Live capture (recommended - measures your actual takeoff motion):
       python3 diagnose_bank.py --port /dev/tty.usbmodemXXXX --seconds 45
     Perform several separate takeoff-style pitch-up motions during the
     recording window (tilt back, hold briefly, return flat, pause, repeat).

  2. Analyze a log already captured with `monitor.py --log run1.csv`:
       python3 diagnose_bank.py --log run1.csv

IMPORTANT: only one process can read the glove's serial port at a time -
close Unity (or unplug/replug the glove) before live-capturing, and note
this measures the same physical gesture you'd use in flight, not a
simultaneous in-Unity recording.

Requires pyserial for --port mode (not needed for --log mode):
    pip install pyserial
"""

import argparse
import csv
import statistics
import sys
import time

import monitor  # reused for the serial link + Sample format

NEUTRAL_DEG = 2.5  # "hand roughly flat" - just above the firmware's own deadzone
EVENT_START_DEG = 4.0  # pitch magnitude that counts as a deliberate motion, not noise
MIN_EVENT_SAMPLES = 5
ONSET_LOOKBACK_SECONDS = 2.0
MIN_CALM_SECONDS = 2.0
MIN_RELIABLE_PITCH_DELTA = 8.0  # below this, roll/pitch ratio is too noise-amplified to trust
ANGLE_RUNAWAY_DEG = 50.0  # well past MAX_HUB_PITCH_DEG - itself a sign the filter's running away


def load_log(path):
    samples = []
    with open(path, newline="") as f:
        reader = csv.DictReader(f)
        if reader.fieldnames is None or "hubPitch" not in reader.fieldnames:
            print(f"'{path}' doesn't look like a monitor.py --log CSV for the current firmware "
                  f"(missing 'hubPitch' column). Re-record with the updated monitor.py.", file=sys.stderr)
            sys.exit(1)
        for row in reader:
            samples.append(monitor.Sample(
                t=float(row["t"]),
                hubPitchRaw=float(row["hubPitchRaw"]),
                hubRollRaw=float(row["hubRollRaw"]),
                hubPitch=float(row["hubPitch"]),
                hubRoll=float(row["hubRoll"]),
                accelGHub=float(row["accelGHub"]),
            ))
    return samples


def capture_live(port, baud, seconds, save_log_path):
    ser = monitor.open_serial(port, baud)
    log_writer = monitor.CsvLogger(save_log_path) if save_log_path else None
    switched = False
    samples = []
    try:
        switched = monitor.ensure_debug_mode(ser)
        print(f"\nRecording for {seconds:.0f}s (Ctrl+C to stop early).")
        print("Perform several SEPARATE takeoff-style pitch-up motions now: tilt your hand")
        print("back like you're climbing, hold briefly, return flat, pause ~1s, repeat")
        print("5-8 times, at your normal takeoff speed (not slow-motion). Throw in a couple")
        print("of pitch-down and pure bank-only motions too, and some time just holding")
        print("the hand flat, so there's a clean baseline to compare against.\n")
        deadline = time.monotonic() + seconds
        while time.monotonic() < deadline:
            s = monitor.read_sample(ser)
            if s is None:
                continue
            if log_writer:
                log_writer.write(s)
            samples.append(s)
        print("Done recording.\n")
    except KeyboardInterrupt:
        print("\nStopped early.\n")
    finally:
        if switched:
            ser.write(b"d")
            time.sleep(0.2)
        if log_writer:
            log_writer.close()
        ser.close()
    return samples


def linreg(xs, ys):
    """Least-squares slope/intercept of ys against xs."""
    n = len(xs)
    if n < 2:
        return 0.0, (ys[0] if ys else 0.0)
    mx, my = statistics.mean(xs), statistics.mean(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    if sxx == 0:
        return 0.0, my
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    slope = sxy / sxx
    return slope, my - slope * mx


def pearson(xs, ys):
    n = len(xs)
    if n < 2:
        return 0.0
    mx, my = statistics.mean(xs), statistics.mean(ys)
    sxx = sum((x - mx) ** 2 for x in xs)
    syy = sum((y - my) ** 2 for y in ys)
    if sxx == 0 or syy == 0:
        return 0.0
    sxy = sum((x - mx) * (y - my) for x, y in zip(xs, ys))
    return sxy / ((sxx ** 0.5) * (syy ** 0.5))


def gate_violation_fraction(seg):
    if not seg:
        return 0.0
    bad = sum(1 for s in seg if not (monitor.ACCEL_GATE_LOW_G < s.accelGHub < monitor.ACCEL_GATE_HIGH_G))
    return bad / len(seg)


def find_events(samples):
    """Stretches where hub pitch moves past EVENT_START_DEG away from a
    recent near-flat baseline, i.e. a deliberate pitch-up/down motion like a
    takeoff pull. Returns onset (last near-flat sample before the move) and
    peak (largest |pitch| reached) for each."""
    events = []
    n = len(samples)
    i = 0
    while i < n:
        if abs(samples[i].hubPitch) > EVENT_START_DEG:
            run_start = i
            while i < n and abs(samples[i].hubPitch) > EVENT_START_DEG:
                i += 1
            run_end = i - 1
            if run_end - run_start + 1 >= MIN_EVENT_SAMPLES:
                onset_idx = None
                for j in range(run_start, -1, -1):
                    if samples[run_start].t - samples[j].t > ONSET_LOOKBACK_SECONDS:
                        break
                    if abs(samples[j].hubPitch) < NEUTRAL_DEG:
                        onset_idx = j
                        break
                if onset_idx is not None:
                    seg = samples[onset_idx:run_end + 1]
                    peak_offset = max(range(len(seg)), key=lambda k: abs(seg[k].hubPitch))
                    events.append({
                        "onset": seg[0],
                        "peak": seg[peak_offset],
                        "segment": seg[:peak_offset + 1],
                    })
        else:
            i += 1
    return events


def find_calm_segments(samples):
    """Stretches of at least MIN_CALM_SECONDS where the hand stayed roughly
    flat - used as the baseline for gate-violation rate and gyro-drift."""
    segments = []
    n = len(samples)
    i = 0
    while i < n:
        if abs(samples[i].hubPitch) < NEUTRAL_DEG:
            start = i
            while i < n and abs(samples[i].hubPitch) < NEUTRAL_DEG:
                i += 1
            seg = samples[start:i]
            if len(seg) >= 2 and seg[-1].t - seg[0].t >= MIN_CALM_SECONDS:
                segments.append(seg)
        else:
            i += 1
    return segments


def analyze(samples):
    events = find_events(samples)
    event_stats = []
    for e in events:
        pitch_delta = e["peak"].hubPitch - e["onset"].hubPitch
        roll_delta = e["peak"].hubRoll - e["onset"].hubRoll
        if abs(pitch_delta) < 1e-6:
            continue
        event_stats.append({
            "pitch_delta": pitch_delta,
            "roll_delta": roll_delta,
            "ratio": roll_delta / pitch_delta,
            "gate_frac": gate_violation_fraction(e["segment"]),
            "duration": e["peak"].t - e["onset"].t,
            "reliable": abs(pitch_delta) >= MIN_RELIABLE_PITCH_DELTA,
            "runaway": abs(pitch_delta) >= ANGLE_RUNAWAY_DEG,
        })

    calm_segments = find_calm_segments(samples)
    calm_gate_fracs = [gate_violation_fraction(seg) for seg in calm_segments]
    drift_rates = []
    for seg in calm_segments:
        if len(seg) < 10:
            continue
        t0 = seg[0].t
        xs = [s.t - t0 for s in seg]
        ys = [s.hubRoll for s in seg]
        slope, _ = linreg(xs, ys)
        drift_rates.append(slope)

    return {
        "event_stats": event_stats,
        "calm_gate_fracs": calm_gate_fracs,
        "drift_rates": drift_rates,
    }


def print_report(result):
    event_stats = result["event_stats"]
    calm_gate_fracs = result["calm_gate_fracs"]
    drift_rates = result["drift_rates"]

    print("=" * 78)
    print("TAKEOFF PITCH -> ROLL BANK DIAGNOSIS")
    print("=" * 78)

    if not event_stats:
        print("\nNo clean pitch-up/down events found (need at least "
              f"{MIN_EVENT_SAMPLES} samples past {EVENT_START_DEG:.1f} deg, starting from a "
              f"near-flat baseline within {ONSET_LOOKBACK_SECONDS:.0f}s).")
        print("Re-run and make sure to: hold the hand flat between reps, then pitch up more")
        print("decisively (closer to your actual takeoff motion) for at least a few tenths of a second.")
        return

    print(f"\nFound {len(event_stats)} pitch event(s):\n")
    print(f"{'#':>3}  {'Δpitch(deg)':>12}  {'Δroll(deg)':>11}  {'ratio':>8}  {'accel-gate-out%':>16}  flag")
    for idx, e in enumerate(event_stats, 1):
        flag = "RUNAWAY" if e["runaway"] else ("" if e["reliable"] else "small Δpitch, noisy ratio")
        print(f"{idx:>3}  {e['pitch_delta']:>12.2f}  {e['roll_delta']:>11.2f}  "
              f"{e['ratio']:>8.3f}  {e['gate_frac'] * 100:>15.1f}%  {flag}")

    runaway_events = [e for e in event_stats if e["runaway"]]
    if runaway_events:
        print(f"\n{len(runaway_events)} of {len(event_stats)} event(s) show |Δpitch| >= "
              f"{ANGLE_RUNAWAY_DEG:.0f} deg - well past the firmware's designed ±"
              f"{30:.0f} deg control range (MAX_HUB_PITCH_DEG). flight_controller.ino's own comments")
        print(f"document 50-100+ deg of hubPitch drift as the bench-confirmed signature of the filter")
        print(f"running ungated on pure gyro integration for too long, i.e. the angle estimate running")
        print(f"away rather than tracking your hand faithfully. Treat those events' ratios as unreliable -")
        print(f"they're measuring filter drift as much as (or instead of) real hand motion.")

    # excludes small-Δpitch events, where the ratio is mostly division noise
    reliable = [e for e in event_stats if e["reliable"] and not e["runaway"]]
    stats_note = ""
    if len(reliable) < 3:
        stats_note = (f"  (only {len(reliable)} of {len(event_stats)} events were both reliable and "
                       f"non-runaway - treat these stats as provisional)")
        reliable = reliable or event_stats  # fall back to something rather than crashing on empty stats

    ratios = [e["ratio"] for e in reliable]
    pitch_deltas = [e["pitch_delta"] for e in reliable]
    roll_deltas = [e["roll_delta"] for e in reliable]

    mean_ratio = statistics.mean(ratios)
    std_ratio = statistics.pstdev(ratios) if len(ratios) > 1 else 0.0
    same_sign_frac = sum(1 for r in ratios if (r > 0) == (mean_ratio > 0)) / len(ratios)
    r = pearson(pitch_deltas, roll_deltas)
    mean_gate_frac_events = statistics.mean(e["gate_frac"] for e in event_stats)
    mean_gate_frac_calm = statistics.mean(calm_gate_fracs) if calm_gate_fracs else 0.0
    mean_abs_drift = statistics.mean(abs(d) for d in drift_rates) if drift_rates else 0.0

    print(f"\nMean roll/pitch ratio (reliable events only): {mean_ratio:+.3f} (stdev {std_ratio:.3f}, "
          f"{same_sign_frac * 100:.0f}% same sign){stats_note}")
    r_caveat = "  (not meaningful - a line always fits <=2 points exactly)" if len(ratios) <= 2 else ""
    print(f"Correlation between Δpitch and Δroll: r = {r:+.2f}{r_caveat}")
    print(f"Accelerometer outside trust gate during events: {mean_gate_frac_events * 100:.1f}%  "
          f"(vs {mean_gate_frac_calm * 100:.1f}% while hand held still)")
    if drift_rates:
        print(f"Roll drift while hand held flat: {mean_abs_drift:.3f} deg/s average "
              f"across {len(drift_rates)} calm stretch(es)")
    else:
        print("Roll drift while hand held flat: not enough calm (flat, >=2s) stretches captured to measure")
        print("  -> recommend re-running with a few deliberate 3+ second dead-still holds mixed in, so")
        print("     this can be checked - it's the cleanest way to see if the sensor/mounting is stable")
        print("     independent of motion.")

    print("\n" + "-" * 78)
    print("VERDICT")
    print("-" * 78)

    findings = []

    # bad readings at rest point at hardware, not motion aliasing - check this first
    baseline_unreliable = mean_gate_frac_calm > 0.5
    if baseline_unreliable:
        findings.append("baseline_hw")
        print(f"\n[ACCELEROMETER UNRELIABLE EVEN AT REST - LIKELY HARDWARE, NOT MOTION]")
        print(f"{mean_gate_frac_calm * 100:.0f}% of samples were outside the trusted 1g band while "
              f"the hand was held still - not moving fast enough for linear acceleration to explain "
              f"it. If the accelerometer basically never reads near 1g at rest, no amount of filter "
              f"or gate tuning will fix this; the sensor data itself is bad going in.")
        print(f"\n  CHECK: reflash flight_controller.ino if you haven't since the last update, then run")
        print(f"    python3 monitor.py --port <port>")
        print(f"  (plain live dashboard, not --guided) and set the glove down flat and UNTOUCHED on a")
        print(f"  table (not held - to rule out hand tremor) for 15-20s. Watch accelG: it should settle")
        print(f"  near 1.00 and stay there. If it sits far from 1.00, jumps around, or the HUB pitch/roll")
        print(f"  numbers keep climbing even though nothing is touching it, gently wiggle each connector")
        print(f"  (Nano<->TCA9548A, TCA9548A<->MPU-6050: SDA/SCL/VCC/GND) one at a time while watching -")
        print(f"  a glitch exactly when you touch one wire pinpoints a loose connection.")

    # a fixed ratio shows up as either a strong Δpitch/Δroll correlation or a tight ratio band
    relative_spread = (std_ratio / abs(mean_ratio)) if mean_ratio != 0 else float("inf")
    mechanical = (len(ratios) >= 3 and same_sign_frac >= 0.75
                  and (abs(r) > 0.6 or relative_spread < 0.35))
    if mechanical:
        findings.append("mechanical")
        print(f"\n[MECHANICAL WRIST COUPLING - primary suspect]")
        print(f"Roll consistently moves proportionally with pitch (ratio {mean_ratio:+.3f} "
              f"+/- {std_ratio:.3f}, r={r:+.2f}, {same_sign_frac * 100:.0f}% sign-consistent). "
              f"This matches ordinary "
              f"wrist anatomy - extending/flexing the wrist to pitch up drags in a few degrees of "
              f"real roll - which is exactly what PITCH_TO_ROLL_COUPLING in flight_controller.ino "
              f"exists to cancel. It's currently 0.0 (never measured for your hand).")
        print(f"\n  FIX: in flight_controller.ino, set")
        print(f"    const float PITCH_TO_ROLL_COUPLING = {mean_ratio:+.3f}f;")
        print(f"  (currently `const float PITCH_TO_ROLL_COUPLING = 0.0f;` around line 116), "
              f"reflash, and re-run this script to confirm the ratio drops toward 0.")

    gate_flag = (not baseline_unreliable and mean_gate_frac_events > 0.30
                 and mean_gate_frac_events > 2 * mean_gate_frac_calm + 0.05)
    if gate_flag:
        findings.append("gate")
        print(f"\n[ACCELEROMETER TRUST-GATE VIOLATIONS DURING THE PULL]")
        print(f"The accelerometer reads outside its trusted 1g band "
              f"({mean_gate_frac_events * 100:.0f}% of samples during pitch events vs "
              f"{mean_gate_frac_calm * 100:.0f}% at rest) - your takeoff pull is fast enough that "
              f"linear hand acceleration is aliasing into the reading, so the complementary filter "
              f"falls back to pure gyro integration for a chunk of exactly the maneuver that's "
              f"banking. This can add roll movement on top of (or look similar to) mechanical "
              f"coupling.")
        print(f"\n  FIX: try a slightly less abrupt takeoff pull and see if the bank lessens. If it")
        print(f"  doesn't change much, this is a secondary contributor and the PITCH_TO_ROLL_COUPLING")
        print(f"  fix above (measured from this same real motion) already accounts for it.")

    drift_flag = mean_abs_drift > 0.5  # a calibrated MPU-6050 should sit well under 1 deg/s
    if drift_flag:
        findings.append("drift")
        severe = mean_abs_drift > 3.0
        print(f"\n[{'SEVERE ' if severe else ''}ROLL DRIFT WHILE HAND HELD FLAT]")
        print(f"Roll drifts ~{mean_abs_drift:.2f} deg/s even while the hand is held flat.")
        if severe:
            print(f"This is far larger than an ordinary uncorrected gyro-bias residual (normally well")
            print(f"under 1 deg/s after calibrate()'s averaging) - recalibrating probably won't fix a")
            print(f"drift this large by itself. More likely either calibrate() didn't get a clean, truly")
            print(f"still ~2s window to measure bias from, or this is the same underlying hardware issue")
            print(f"flagged above (bad sensor data feeds bad bias measurement too).")
            print(f"\n  FIX: rule out the hardware check above first. Then retry calibration: reconnect (or")
            print(f"  press 'c') with the glove resting completely still on a table, not in your hand, for")
            print(f"  the full ~2s window - any motion during calibrate() corrupts the bias measurement.")
        else:
            print(f"calibrate()'s gyro bias removal isn't fully cancelling this chip's zero-rate offset")
            print(f"(it varies with die temperature, so a calibration done cold minutes before flying can")
            print(f"drift by takeoff time).")
            print(f"\n  FIX: recalibrate (press 'c' or reconnect) right before you start flying, not")
            print(f"  earlier in the session, and keep the hand fully still for the full ~2s calibration.")

    if not findings:
        print("\nNo single cause stood out clearly from this data (ratio inconsistent across events,")
        print("gate violations not notably elevated during events, and no significant flat-hand drift).")
        print("Capture more reps (aim for 8-10 clean pitch-up motions) and re-run - a noisy first pass")
        print("can hide a real but smaller effect.")

    print()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    src = ap.add_mutually_exclusive_group(required=True)
    src.add_argument("--port", help="Serial port to live-capture from, e.g. /dev/tty.usbmodemXXXX")
    src.add_argument("--log", help="Path to an existing monitor.py --log CSV to analyze instead of live-capturing")
    ap.add_argument("--baud", type=int, default=115200)
    ap.add_argument("--seconds", type=float, default=45.0, help="Live capture duration (default 45s)")
    ap.add_argument("--save-log", help="Also write the live-captured samples to this CSV path (monitor.py-compatible)")
    args = ap.parse_args()

    if args.log:
        samples = load_log(args.log)
    else:
        samples = capture_live(args.port, args.baud, args.seconds, args.save_log)

    if len(samples) < MIN_EVENT_SAMPLES:
        print(f"Only got {len(samples)} sample(s) - not enough to analyze. Check the connection "
              f"and try again.", file=sys.stderr)
        sys.exit(1)

    result = analyze(samples)
    print_report(result)


if __name__ == "__main__":
    main()
