#!/usr/bin/env python3
"""
Records labeled gesture training data from the glove for train_gesture_model.py.

Needs gesture_capture.ino flashed (not flight_controller.ino or
gesture_classifier.ino) - it's the sketch that streams raw, unlabeled IMU
samples for this script to label.

Two label groups: "pose" (hub sensor - neutral/climb/dive/bank_left/bank_right
plus the four diagonal combos) and "throttle" (fingers -
neutral/extend/curl). Prompts you into position for each label, records a
fixed hold per rep, and appends to the CSV rather than overwriting, so you
can run it repeatedly to build up more data.

A few things that'll quietly ruin a recording session if you forget them:
only one process can hold the serial port at a time (close Unity first);
readings are raw/uncalibrated, so retrain if you remount the glove
differently; and a disconnected finger sensor won't error, it'll just record
garbage.

Usage:
    python3 capture_gesture_data.py --port /dev/tty.usbmodemXXXX
    python3 capture_gesture_data.py --port /dev/tty.usbmodemXXXX --groups throttle --reps 5
    python3 capture_gesture_data.py --port /dev/tty.usbmodemXXXX --out gesture_data.csv --hold-seconds 8

Output CSV columns:
    group,label,rep_id,sample_idx,
    hAX,hAY,hAZ,hGX,hGY,hGZ, iAX,iAY,iAZ,iGX,iGY,iGZ, mAX,mAY,mAZ,mGX,mGY,mGZ
(h = hub, i = index finger, m = middle finger)
"""

import argparse
import csv
import os
import re
import sys
import time

try:
    import serial
except ImportError:
    print("pyserial is required: pip install pyserial", file=sys.stderr)
    sys.exit(1)

RAW_LINE_RE = re.compile(r"^" + ",".join([r"(-?[\d.]+)"] * 18) + r"$")

HEADER = (
    ["group", "label", "rep_id", "sample_idx"]
    + ["hAX", "hAY", "hAZ", "hGX", "hGY", "hGZ"]
    + ["iAX", "iAY", "iAZ", "iGX", "iGY", "iGZ"]
    + ["mAX", "mAY", "mAZ", "mGX", "mGY", "mGZ"]
)

GROUPS = {
    "pose": {
        "labels": [
            "neutral", "climb", "dive", "bank_left", "bank_right",
            "climb_bank_left", "climb_bank_right", "dive_bank_left", "dive_bank_right",
        ],
        "instructions": {
            "neutral": "Hold your hand flat and level (normal cruise pose).",
            "climb": "Tilt your whole hand back like you're pulling into a climb, and hold.",
            "dive": "Tilt your whole hand forward like you're pushing into a dive, and hold.",
            "bank_left": "Bank/roll your whole hand to the left, and hold.",
            "bank_right": "Bank/roll your whole hand to the right, and hold.",
            "climb_bank_left": "Tilt back into a climb AND bank/roll left at the same time, and hold.",
            "climb_bank_right": "Tilt back into a climb AND bank/roll right at the same time, and hold.",
            "dive_bank_left": "Tilt forward into a dive AND bank/roll left at the same time, and hold.",
            "dive_bank_right": "Tilt forward into a dive AND bank/roll right at the same time, and hold.",
        },
    },
    "throttle": {
        "labels": ["neutral", "extend", "curl"],
        "instructions": {
            "neutral": "Relax your index and middle fingers to a natural, in-between curl (not fully extended, not curled back), and hold.",
            "extend": "Straighten/extend both your index and middle fingers, and hold.",
            "curl": "Curl both your index and middle fingers back toward your palm, and hold.",
        },
    },
}


def open_serial(port, baud):
    ser = serial.Serial(port, baud, timeout=1)
    time.sleep(2)  # let the board reset after the port opens
    ser.reset_input_buffer()
    return ser


def read_raw_sample(ser):
    raw = ser.readline().decode(errors="replace").strip()
    if not raw:
        return None
    m = RAW_LINE_RE.match(raw)
    if not m:
        return None
    return tuple(float(x) for x in m.groups())


def collect_for(ser, seconds):
    samples = []
    deadline = time.monotonic() + seconds
    while time.monotonic() < deadline:
        s = read_raw_sample(ser)
        if s is None:
            continue
        samples.append(s)
    return samples


def check_existing_header(path):
    """Bail out instead of appending mismatched rows under an old CSV schema."""
    with open(path, newline="") as f:
        existing_header = next(csv.reader(f), None)
    if existing_header != HEADER:
        print(f"'{path}' already exists but its header doesn't match the current format "
              f"(hub+index+middle, group+label columns).", file=sys.stderr)
        print(f"  Expected: {','.join(HEADER)}", file=sys.stderr)
        print(f"  Found:    {','.join(existing_header or [])}", file=sys.stderr)
        print("This is almost certainly data recorded before the finger sensors were added - "
              "pick a new --out path (e.g. gesture_data_v2.csv) rather than appending, or rename/archive "
              "the old file yourself first if you want to keep the name.", file=sys.stderr)
        sys.exit(1)


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--port", required=True, help="Serial port, e.g. /dev/tty.usbmodemXXXX")
    ap.add_argument("--baud", type=int, default=115200)
    ap.add_argument("--out", default="gesture_data.csv", help="Dataset CSV to append to (default gesture_data.csv)")
    ap.add_argument("--hold-seconds", type=float, default=8.0,
                     help="Recording duration per gesture per rep (default 8s)")
    ap.add_argument("--reps", type=int, default=3,
                     help="How many separate reps to record per gesture (default 3 - get in and out of position between each, for variety)")
    ap.add_argument("--groups", default="pose,throttle",
                     help="Comma-separated label groups to record this session (default: pose,throttle)")
    ap.add_argument("--labels", default=None,
                     help="Comma-separated labels to record, in order - restricts to specific labels within "
                          "the selected group(s) instead of recording all of them (e.g. --groups throttle "
                          "--labels extend to redo just one)")
    args = ap.parse_args()

    groups = [g.strip() for g in args.groups.split(",") if g.strip()]
    unknown_groups = [g for g in groups if g not in GROUPS]
    if unknown_groups:
        print(f"Unknown group(s) {unknown_groups} - choose from {list(GROUPS)}.", file=sys.stderr)
        sys.exit(1)

    label_filter = None
    if args.labels:
        label_filter = {l.strip() for l in args.labels.split(",") if l.strip()}

    file_exists = os.path.exists(args.out)
    if file_exists:
        check_existing_header(args.out)

    ser = open_serial(args.port, args.baud)

    plan = [(group, label) for group in groups for label in GROUPS[group]["labels"]
            if label_filter is None or label in label_filter]
    print(f"Recording to '{args.out}' ({'appending' if file_exists else 'new file'}).")
    print(f"{args.reps} rep(s) x {args.hold_seconds:.0f}s per gesture: {plan}\n")

    try:
        with open(args.out, "a", newline="") as f:
            writer = csv.writer(f)
            if not file_exists:
                writer.writerow(HEADER)

            for group, label in plan:
                instruction = GROUPS[group]["instructions"][label]
                for rep in range(1, args.reps + 1):
                    input(f"[{group}:{label} - rep {rep}/{args.reps}] {instruction}\n"
                          f"  Get in position, then press Enter to start recording "
                          f"({args.hold_seconds:.0f}s)...")
                    print("  Recording...", end="", flush=True)
                    ser.reset_input_buffer()
                    samples = collect_for(ser, args.hold_seconds)
                    print(f" got {len(samples)} samples.")
                    if not samples:
                        print("  WARNING: no data received - check the connection. Skipping.\n")
                        continue
                    rep_id = int(time.time() * 1000)  # keeps windows from straddling two reps
                    for idx, values in enumerate(samples):
                        writer.writerow([group, label, rep_id, idx] + list(values))
                    f.flush()
                    print()
    except KeyboardInterrupt:
        print("\nStopped early - whatever was already recorded is saved.")
    finally:
        ser.close()

    print(f"Done. Dataset: {args.out}")
    print("Next: python3 train_gesture_model.py --data " + args.out + " --group pose")
    print("      python3 train_gesture_model.py --data " + args.out + " --group throttle")


if __name__ == "__main__":
    main()
