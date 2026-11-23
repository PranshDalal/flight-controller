#!/usr/bin/env python3
"""
Removes stretches of gesture_data.csv where the hand wasn't holding the
labeled gesture, and writes gesture_data_clean.csv. The original file is
never modified.

Found by plotting index-finger accel Z through each throttle recording
(neutral sits near 0 g, extend near -0.65 g):
  - neutral rep 1: fingers extended (~ -0.72 g) for most of the recording,
    then drifting back - mostly not neutral, so the whole rep is dropped
  - extend rep 1: fingers still neutral (~ 0 g) for the first ~10 s
  - neutral rep 4: fingers extended (~ -0.48 g) for the first ~5 s
  - extend rep 4: ~1 s moving into position
Pose recordings: lead-in trims, see the comment above the pose entries.

Usage:
    python3 clean_gesture_data.py [--data gesture_data.csv]
                                  [--out gesture_data_clean.csv]
"""
import argparse
import csv

# (group, label, rep_id): samples with sample_idx in [start, end) are dropped;
# end=None means to the end of the rep.
DROP = {
    ("throttle", "neutral", "1785349646384"): [(0, None)],   # rep 1
    ("throttle", "extend", "1785349695892"): [(0, 720)],     # rep 1
    ("throttle", "neutral", "1786898493595"): [(0, 360)],    # rep 4
    ("throttle", "extend", "1786898527930"): [(0, 120)],     # rep 4

    # Pose lead-ins: the first rep of each gesture in each session (reps 1
    # and 4) starts with the hand still in the previous gesture or passing
    # through neutral. Trimmed in 60-sample (~0.9 s) chunks until the hub
    # reading is first nearer this gesture than any other. Mid-rep wobble is
    # kept - that's real variation the model should learn.
    ("pose", "climb", "1785349487295"): [(0, 60)],             # rep 1
    ("pose", "climb", "1786898162351"): [(0, 120)],            # rep 4
    ("pose", "dive", "1785349522916"): [(0, 60)],              # rep 1
    ("pose", "dive", "1786898196932"): [(0, 180)],             # rep 4
    ("pose", "bank_left", "1785349556238"): [(0, 120)],        # rep 1
    ("pose", "bank_left", "1786898239263"): [(0, 120)],        # rep 4
    ("pose", "bank_right", "1785349588112"): [(0, 60)],        # rep 1
    ("pose", "bank_right", "1786898274044"): [(0, 180)],       # rep 4
    ("pose", "climb_bank_left", "1785425730844"): [(0, 240)],  # rep 1
    ("pose", "climb_bank_left", "1786898315486"): [(0, 420)],  # rep 4
    ("pose", "climb_bank_right", "1785425772873"): [(0, 240)], # rep 1
    ("pose", "climb_bank_right", "1786898351608"): [(0, 120)], # rep 4
    ("pose", "dive_bank_left", "1785425820353"): [(0, 120)],   # rep 1
    ("pose", "dive_bank_left", "1786898394198"): [(0, 180)],   # rep 4
    ("pose", "dive_bank_right", "1785425869585"): [(0, 240)],  # rep 1
    ("pose", "dive_bank_right", "1786898445925"): [(0, 960)],  # rep 4 (~14 s)
}


def dropped(key, idx):
    for start, end in DROP.get(key, []):
        if idx >= start and (end is None or idx < end):
            return True
    return False


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", default="gesture_data.csv")
    ap.add_argument("--out", default="gesture_data_clean.csv")
    a = ap.parse_args()

    kept = removed = 0
    with open(a.data, newline="") as fin, open(a.out, "w", newline="") as fout:
        reader = csv.DictReader(fin)
        writer = csv.DictWriter(fout, fieldnames=reader.fieldnames)
        writer.writeheader()
        for row in reader:
            key = (row["group"], row["label"], row["rep_id"])
            if dropped(key, int(row["sample_idx"])):
                removed += 1
            else:
                writer.writerow(row)
                kept += 1
    print(f"kept {kept} samples, removed {removed} -> {a.out}")


if __name__ == "__main__":
    main()
