#!/usr/bin/env python3
"""
Interactive serial monitor / diagnostic tool for the flight-controller glove.

Talks to flight_controller.ino's 'd' debug command, which swaps the normal
CSV stream for a verbose raw-angle dump. Two modes: a live dashboard
(default) showing raw/relative angles and the final pitch/roll axes with bar
graphs, or --guided, which walks through tilt/bank motions and reports how
much cross-axis bleed each one causes.

Auto-detects whether the firmware's in plain CSV or debug mode, switches it
if needed, and switches it back on exit so it doesn't leave the board in
debug mode for your next Unity session.

Only one process can hold the serial port at a time - close Unity first.

Requires pyserial:
    pip install pyserial

Usage:
    python3 monitor.py --list                      # list available serial ports
    python3 monitor.py --port /dev/tty.usbmodemXXXX
    python3 monitor.py --port /dev/tty.usbmodemXXXX --guided
    python3 monitor.py --port /dev/tty.usbmodemXXXX --log run1.csv
"""

import argparse
import re
import sys
import time
from dataclasses import dataclass, fields

try:
    import serial
    from serial.tools import list_ports
except ImportError:
    print("pyserial is required: pip install pyserial", file=sys.stderr)
    sys.exit(1)

# mirrors flight_controller.ino - keep in sync if the firmware's tuning changes
MAX_HUB_PITCH_DEG = 30.0
MAX_HUB_ROLL_DEG = 35.0
HUB_PITCH_DEADZONE = 0.08
HUB_ROLL_DEADZONE = 0.08
ACCEL_GATE_LOW_G = 0.85
ACCEL_GATE_HIGH_G = 1.15

DEBUG_LINE_RE = re.compile(
    r"HUB pitch=(?P<hubPitchRaw>-?[\d.]+) roll=(?P<hubRollRaw>-?[\d.]+) "
    r"\|\| hubPitch=(?P<hubPitch>-?[\d.]+) hubRoll=(?P<hubRoll>-?[\d.]+) "
    r"\|\| accelG=(?P<accelGHub>-?[\d.]+)"
)
CSV_LINE_RE = re.compile(r"^-?[\d.]+,-?[\d.]+$")


@dataclass
class Sample:
    t: float
    hubPitchRaw: float
    hubRollRaw: float
    hubPitch: float
    hubRoll: float
    accelGHub: float


def deadzone(v, dz):
    if abs(v) < dz:
        return 0.0
    return (v - (dz if v > 0 else -dz)) / (1.0 - dz)


def clamp1(v):
    return max(-1.0, min(1.0, v))


def axes_from_sample(s: Sample):
    pitch = clamp1(deadzone(s.hubPitch / MAX_HUB_PITCH_DEG, HUB_PITCH_DEADZONE))
    roll = clamp1(deadzone(s.hubRoll / MAX_HUB_ROLL_DEG, HUB_ROLL_DEADZONE))
    return pitch, roll


ACTION_THRESHOLD = 0.15  # past deadzone by this much before we call it out, so jitter doesn't flicker


def active_labels(pitch, roll):
    """Gesture names for whatever's past ACTION_THRESHOLD. If a label reads
    backwards on your hardware, swap that axis's pair of strings."""
    labels = []
    if roll > ACTION_THRESHOLD:
        labels.append("BANK RIGHT")
    elif roll < -ACTION_THRESHOLD:
        labels.append("BANK LEFT")
    if pitch > ACTION_THRESHOLD:
        labels.append("PITCH UP (climb)")
    elif pitch < -ACTION_THRESHOLD:
        labels.append("PITCH DOWN (dive)")
    return labels or ["neutral"]


def parse_debug_line(line: str):
    m = DEBUG_LINE_RE.search(line)
    if not m:
        return None
    d = {k: float(v) for k, v in m.groupdict().items()}
    return Sample(t=time.monotonic(), **d)


def bar(value, limit, width=24):
    """Text bar for a value in [-limit, limit], zero in the middle."""
    value = max(-limit, min(limit, value))
    mid = width // 2
    pos = mid + int(round((value / limit) * mid))
    pos = max(0, min(width, pos))
    chars = ["-"] * (width + 1)
    chars[mid] = "|"
    chars[pos] = "#"
    return "[" + "".join(chars) + "]"


def list_serial_ports():
    ports = list(list_ports.comports())
    if not ports:
        print("No serial ports found.")
        return
    for p in ports:
        print(f"{p.device}\t{p.description}")


def open_serial(port, baud):
    ser = serial.Serial(port, baud, timeout=1)
    time.sleep(2)  # let the board reset after the port opens
    ser.reset_input_buffer()
    return ser


def detect_mode(ser, timeout=6.0):
    """'debug', 'csv', or None if nothing's arrived yet (still calibrating?)."""
    deadline = time.monotonic() + timeout
    while time.monotonic() < deadline:
        raw = ser.readline().decode(errors="replace").strip()
        if not raw:
            continue
        if DEBUG_LINE_RE.search(raw):
            return "debug"
        if CSV_LINE_RE.match(raw):
            return "csv"
        # boot banner / calibration messages - keep reading
    return None


def ensure_debug_mode(ser):
    """True if this call switched the firmware into debug mode (so we know to switch it back)."""
    mode = detect_mode(ser)
    if mode == "debug":
        return False
    print("Switching firmware into debug mode ('d')...")
    ser.write(b"d")
    mode = detect_mode(ser, timeout=6.0)  # confirm it actually took
    if mode != "debug":
        print("Warning: could not confirm debug mode - check the board/port.", file=sys.stderr)
    return True


def read_sample(ser):
    raw = ser.readline().decode(errors="replace").strip()
    if not raw:
        return None
    return parse_debug_line(raw)


def run_live(ser, log_writer):
    print("Live dashboard - Ctrl+C to quit.\n")
    last_draw = 0.0
    while True:
        s = read_sample(ser)
        if s is None:
            continue
        if log_writer:
            log_writer.write(s)
        now = time.monotonic()
        if now - last_draw < 0.08:  # cap redraw rate, serial arrives faster than eyes read
            continue
        last_draw = now
        pitch, roll = axes_from_sample(s)

        def trust_flag(g):
            return "" if ACCEL_GATE_LOW_G < g < ACCEL_GATE_HIGH_G else "  <-- OUTSIDE GATE (pure gyro, no correction)"

        lines = [
            "ACTION: " + ", ".join(active_labels(pitch, roll)).upper(),
            "",
            f"HUB  pitch={s.hubPitchRaw:7.2f}  roll={s.hubRollRaw:7.2f}   (rel: hubPitch={s.hubPitch:7.2f} hubRoll={s.hubRoll:7.2f} deg)",
            "",
            f"accelG HUB={s.accelGHub:5.2f}{trust_flag(s.accelGHub)}",
            "",
            f"pitch  {bar(pitch, 1.0)} {pitch:+.3f}",
            f"roll   {bar(roll, 1.0)} {roll:+.3f}",
        ]
        sys.stdout.write("\x1b[2J\x1b[H")  # clear screen, home cursor
        sys.stdout.write("\n".join(lines) + "\n")
        sys.stdout.flush()


class CsvLogger:
    def __init__(self, path):
        self.f = open(path, "w")
        cols = [f.name for f in fields(Sample)] + ["pitchAxis", "rollAxis"]
        self.f.write(",".join(cols) + "\n")

    def write(self, s: Sample):
        pitch, roll = axes_from_sample(s)
        vals = [getattr(s, f.name) for f in fields(Sample)] + [pitch, roll]
        self.f.write(",".join(f"{v:.4f}" if isinstance(v, float) else str(v) for v in vals) + "\n")
        self.f.flush()

    def close(self):
        self.f.close()


# (label, instruction, field measured)
GUIDED_STEPS = [
    ("Baseline", "Hold hand flat, fingers straight (neutral pose). This also re-calibrates the firmware.", None),
    ("Hand tilt FORWARD", "Tilt the whole hand forward (nose-down) and hold.", "hubPitch"),
    ("Hand tilt BACK", "Tilt the whole hand back (nose-up, like a takeoff climb) and hold.", "hubPitch"),
    ("Hand bank LEFT", "Bank/roll the whole hand left and hold.", "hubRoll"),
    ("Hand bank RIGHT", "Bank/roll the whole hand right and hold.", "hubRoll"),
]

# pairs that should move their primary field in opposite directions - if a pair comes back
# with the same sign, something about the rep (or session drift) makes it untrustworthy
OPPOSITE_STEP_PAIRS = [
    ("Hand tilt FORWARD", "Hand tilt BACK"),
    ("Hand bank LEFT", "Hand bank RIGHT"),
]

FIELD_AXIS_NAME = {"hubPitch": "pitch", "hubRoll": "roll"}
FIELD_LIMIT = {"hubPitch": MAX_HUB_PITCH_DEG, "hubRoll": MAX_HUB_ROLL_DEG}
FIELD_DEADZONE = {"hubPitch": HUB_PITCH_DEADZONE, "hubRoll": HUB_ROLL_DEADZONE}


def collect_for(ser, seconds, log_writer):
    samples = []
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        s = read_sample(ser)
        if s is None:
            continue
        if log_writer:
            log_writer.write(s)
        samples.append(s)
    return samples


def mean(vals):
    return sum(vals) / len(vals) if vals else 0.0


def run_guided(ser, hold_seconds, log_writer):
    print("Guided cross-axis bleed test.")
    print(f"For each motion: get in position, press Enter, then hold still for {hold_seconds:.0f}s.\n")

    baseline_means = {"hubPitch": 0.0, "hubRoll": 0.0}
    results = []  # (label, primary_field, deltas dict)

    for label, instruction, primary_field in GUIDED_STEPS:
        input(f"[{label}] {instruction}\n  Press Enter when ready...")

        if primary_field is None:
            # recalibrate before the baseline, not after - otherwise every later delta
            # carries a leftover old-zero-vs-new-zero offset
            print("  Recalibrating firmware (keep the hand still)...", end="", flush=True)
            ser.reset_input_buffer()
            ser.write(b"c")
            time.sleep(2.2)  # calibrate() takes ~2s (gyro bias + settle), see flight_controller.ino
            ser.reset_input_buffer()
            print(" done.")

        print("  Capturing...", end="", flush=True)
        samples = collect_for(ser, hold_seconds, log_writer)
        print(f" got {len(samples)} samples.")
        if not samples:
            print("  No data received - check the connection. Skipping this step.\n")
            continue

        means = {
            "hubPitch": mean([s.hubPitch for s in samples]),
            "hubRoll": mean([s.hubRoll for s in samples]),
        }

        if primary_field is None:
            baseline_means = means
            print(f"  Baseline noise floor: hubPitch={means['hubPitch']:+.2f} "
                  f"hubRoll={means['hubRoll']:+.2f}\n")
            continue

        deltas = {k: means[k] - baseline_means[k] for k in means}
        results.append((label, primary_field, deltas))

        primary_delta = deltas[primary_field]
        print(f"  {FIELD_AXIS_NAME[primary_field]} axis moved {primary_delta:+.2f} deg "
              f"(field={primary_field})")

        limit = FIELD_LIMIT[primary_field]
        dz = FIELD_DEADZONE[primary_field]
        if deadzone(primary_delta / limit, dz) == 0.0:
            print(f"  WARNING: that's inside {FIELD_AXIS_NAME[primary_field]}'s own deadzone "
                  f"(~{dz * limit:.1f} deg) - this rep barely registered. Numbers from this step "
                  f"aren't reliable enough to derive a coupling coefficient from; consider "
                  f"re-running --guided and exaggerating this motion more.")
        print()

    print("\n" + "=" * 78)
    print("Bleed report (Δ from baseline; deadzone'd = value after that axis's own deadzone)")
    print("=" * 78)
    for label, primary_field, deltas in results:
        print(f"\n{label}  [primary: {FIELD_AXIS_NAME[primary_field]} = {deltas[primary_field]:+.2f} deg]")
        for field, delta in deltas.items():
            if field == primary_field:
                continue
            limit = FIELD_LIMIT[field]
            dz = FIELD_DEADZONE[field]
            normalized = delta / limit
            after_dz = deadzone(normalized, dz)
            flag = "  <-- CLEARS DEADZONE, will bleed through to the axis" if after_dz != 0.0 else ""
            print(f"    -> {FIELD_AXIS_NAME[field]:8s} ({field}): {delta:+6.2f} deg raw, "
                  f"{after_dz:+.3f} after deadzone{flag}")
    print()

    by_label = {label: (primary_field, deltas) for label, primary_field, deltas in results}
    print("=" * 78)
    print("Consistency check (opposite motions should move their primary field in opposite directions)")
    print("=" * 78)
    any_issue = False
    for label_a, label_b in OPPOSITE_STEP_PAIRS:
        if label_a not in by_label or label_b not in by_label:
            continue
        field_a, deltas_a = by_label[label_a]
        _, deltas_b = by_label[label_b]
        delta_a, delta_b = deltas_a[field_a], deltas_b[field_a]
        if delta_a == 0.0 or delta_b == 0.0:
            continue
        if (delta_a > 0) == (delta_b > 0):
            any_issue = True
            print(f"  MISMATCH: '{label_a}' ({delta_a:+.2f} deg) and '{label_b}' ({delta_b:+.2f} deg) "
                  f"moved {FIELD_AXIS_NAME[field_a]} the SAME direction - these are supposed to be "
                  f"opposite motions. Likely session drift or an inconsistent rep between the two "
                  f"steps; don't derive a coupling coefficient from this pair without re-testing it.")
    if not any_issue:
        print("  All opposite-motion pairs disagreed in sign as expected. Looks trustworthy.")
    print()

    if "Hand tilt BACK" in by_label:
        field_a, deltas = by_label["Hand tilt BACK"]
        dPitch, dRoll = deltas["hubPitch"], deltas["hubRoll"]
        if dPitch:
            print(f"Suggested PITCH_TO_ROLL_COUPLING (from 'Hand tilt BACK'): {dRoll / dPitch:+.3f}f")
    if "Hand bank RIGHT" in by_label:
        field_a, deltas = by_label["Hand bank RIGHT"]
        dRoll, dPitch = deltas["hubRoll"], deltas["hubPitch"]
        if dRoll:
            print(f"Suggested ROLL_TO_PITCH_COUPLING (from 'Hand bank RIGHT'): {dPitch / dRoll:+.3f}f")
    print()


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", help="Serial port, e.g. /dev/tty.usbmodemXXXX or COM5")
    ap.add_argument("--baud", type=int, default=115200)
    ap.add_argument("--list", action="store_true", help="List available serial ports and exit")
    ap.add_argument("--guided", action="store_true", help="Run the scripted cross-axis bleed test instead of the live dashboard")
    ap.add_argument("--hold-seconds", type=float, default=3.0, help="Sample duration per guided step (default 3s)")
    ap.add_argument("--log", help="Path to write a CSV log of every parsed sample")
    args = ap.parse_args()

    if args.list:
        list_serial_ports()
        return

    if not args.port:
        print("Pass --port (use --list to see available ports).", file=sys.stderr)
        sys.exit(1)

    log_writer = CsvLogger(args.log) if args.log else None

    ser = open_serial(args.port, args.baud)
    switched_to_debug = False
    try:
        switched_to_debug = ensure_debug_mode(ser)
        if args.guided:
            run_guided(ser, args.hold_seconds, log_writer)
        else:
            run_live(ser, log_writer)
    except KeyboardInterrupt:
        print("\nExiting.")
    finally:
        if switched_to_debug:
            print("Restoring firmware to its original (non-debug) streaming mode...")
            ser.write(b"d")
            time.sleep(0.2)
        if log_writer:
            log_writer.close()
        ser.close()


if __name__ == "__main__":
    main()
