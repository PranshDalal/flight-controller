#!/usr/bin/env python3
"""
Trains an on-device classifier from labeled glove data recorded by
capture_gesture_data.py, and exports it as a TensorFlow Lite Micro model for
glove/src/gesture_classifier/gesture_classifier.ino.

Trains one of two models per run, picked by --group: "pose" (hub sensor - 9
hand-orientation classes including the diagonal combos) or "throttle"
(fingers - neutral/extend/curl). Separate models since pose and finger curl
are independent and both need to work at once.

Windows each rep with 50% overlap, then splits train/val by rep (not by
window - overlapping windows from the same rep are near-duplicates, so
splitting at the window level leaks and inflates the reported accuracy).
Also stratifies the split per label, since a naive shuffle can otherwise
starve validation of a class that only has a handful of reps.

Usage:
    python3 train_gesture_model.py --data gesture_data.csv --group pose
    python3 train_gesture_model.py --data gesture_data.csv --group throttle
    python3 train_gesture_model.py --data run1.csv run2.csv --group pose --epochs 40
"""

import argparse
import csv
import sys
from collections import defaultdict

try:
    import numpy as np
except ImportError:
    print("numpy is required: pip install numpy", file=sys.stderr)
    sys.exit(1)

try:
    import tensorflow as tf
except ImportError:
    print("tensorflow is required: pip install tensorflow", file=sys.stderr)
    sys.exit(1)

# Must match gesture_capture.ino / capture_gesture_data.py / gesture_classifier.ino.
WINDOW_SAMPLES = 32
ACCEL_NORM_G = 2.0
GYRO_NORM_DPS = 250.0

REQUIRED_COLUMNS = {
    "group", "label", "rep_id", "sample_idx",
    "hAX", "hAY", "hAZ", "hGX", "hGY", "hGZ",
    "iAX", "iAY", "iAZ", "iGX", "iGY", "iGZ",
    "mAX", "mAY", "mAZ", "mGX", "mGY", "mGZ",
}

GROUP_CONFIG = {
    "pose": {
        "labels": [
            "neutral", "climb", "dive", "bank_left", "bank_right",
            "climb_bank_left", "climb_bank_right", "dive_bank_left", "dive_bank_right",
        ],
        "columns": ["hAX", "hAY", "hAZ", "hGX", "hGY", "hGZ"],
        "prefix": "Gesture",
        "model_var": "g_gesture_model",
        "default_tflite": "gesture_model.tflite",
        "default_header": "gesture_model.h",
    },
    "throttle": {
        "labels": ["neutral", "extend", "curl"],
        "columns": ["iAX", "iAY", "iAZ", "iGX", "iGY", "iGZ", "mAX", "mAY", "mAZ", "mGX", "mGY", "mGZ"],
        "prefix": "Throttle",
        "model_var": "g_throttle_model",
        "default_tflite": "throttle_model.tflite",
        "default_header": "throttle_model.h",
    },
}


def load_reps(paths, group, columns):
    """{(label, rep_id): [(col0, col1, ...), ...] sorted by sample_idx}, filtered to --group."""
    reps = defaultdict(list)
    for path in paths:
        with open(path, newline="") as f:
            reader = csv.DictReader(f)
            if reader.fieldnames is None or not REQUIRED_COLUMNS.issubset(reader.fieldnames):
                print(f"'{path}' is missing expected columns {REQUIRED_COLUMNS} - is this a "
                      f"current capture_gesture_data.py output? (Older hub-only recordings use a "
                      f"different schema and need to be re-recorded.)", file=sys.stderr)
                sys.exit(1)
            for row in reader:
                if row["group"] != group:
                    continue
                key = (row["label"], row["rep_id"])
                reps[key].append((int(row["sample_idx"]),) + tuple(float(row[c]) for c in columns))
    for key in reps:
        reps[key].sort(key=lambda r: r[0])
    return reps


def make_windows(reps, labels, num_axes, window, stride):
    """X (N, window, num_axes) float32, y (N,) int, groups (N,) rep ids."""
    X, y, groups = [], [], []
    skipped_labels = set()
    skipped_short = 0
    for (label, rep_id), rows in reps.items():
        if label not in labels:
            skipped_labels.add(label)
            continue
        label_idx = labels.index(label)
        values = np.array([r[1:] for r in rows], dtype=np.float32)
        if len(values) < window:
            skipped_short += 1
            continue
        for start in range(0, len(values) - window + 1, stride):
            X.append(values[start:start + window])
            y.append(label_idx)
            groups.append(rep_id)
    if skipped_labels:
        print(f"Warning: ignoring unrecognized label(s) {skipped_labels} - not in {labels}.",
              file=sys.stderr)
    if skipped_short:
        print(f"Warning: skipped {skipped_short} rep(s) shorter than the {window}-sample window.",
              file=sys.stderr)
    if not X:
        print("No usable windows found - check your data and --window size.", file=sys.stderr)
        sys.exit(1)
    return np.stack(X), np.array(y, dtype=np.int64), np.array(groups)


def normalize(X):
    """Scales each 6-axis [accel3, gyro3] chunk into roughly [-1, 1]."""
    X = X.copy()
    num_axes = X.shape[-1]
    for base in range(0, num_axes, 6):
        X[:, :, base:base + 3] /= ACCEL_NORM_G
        X[:, :, base + 3:base + 6] /= GYRO_NORM_DPS
    return X


def group_split(groups, y, val_fraction, seed):
    """Splits by rep id, stratified per label - a flat shuffle once dropped a
    6-rep class out of validation entirely."""
    rng = np.random.RandomState(seed)
    reps_by_label = defaultdict(set)
    for g, label in zip(groups, y):
        reps_by_label[label].add(g)

    val_groups = set()
    for label, rep_set in reps_by_label.items():
        rep_list = sorted(rep_set)
        rng.shuffle(rep_list)
        n_val = int(round(len(rep_list) * val_fraction)) if len(rep_list) > 1 else 0
        val_groups.update(rep_list[:n_val])

    val_mask = np.array([g in val_groups for g in groups])
    return ~val_mask, val_mask


def confusion_matrix(y_true, y_pred, num_classes):
    cm = np.zeros((num_classes, num_classes), dtype=int)
    for t, p in zip(y_true, y_pred):
        cm[t, p] += 1
    return cm


def print_confusion_matrix(cm, labels):
    width = max(len(l) for l in labels) + 2
    header = " " * width + "".join(f"{l[:8]:>10}" for l in labels)
    print(header)
    for i, row_label in enumerate(labels):
        row = f"{row_label:<{width}}" + "".join(f"{cm[i, j]:>10}" for j in range(len(labels)))
        print(row)


def build_model(window, num_axes, num_classes):
    model = tf.keras.Sequential([
        tf.keras.layers.Input(shape=(window, num_axes)),
        tf.keras.layers.Flatten(),
        tf.keras.layers.Dense(50, activation="relu"),
        tf.keras.layers.Dense(15, activation="relu"),
        tf.keras.layers.Dense(num_classes, activation="softmax"),
    ])
    model.compile(optimizer="adam", loss="sparse_categorical_crossentropy", metrics=["accuracy"])
    return model


def write_c_header(tflite_bytes, header_path, labels, window, num_axes, prefix, model_var):
    with open(header_path, "w") as f:
        f.write("#pragma once\n\n")
        f.write("// Auto-generated by train_gesture_model.py - do not hand-edit.\n")
        f.write(f"// Window: {window} samples x {num_axes} axes, normalized by "
                f"ACCEL_NORM_G={ACCEL_NORM_G}, GYRO_NORM_DPS={GYRO_NORM_DPS}.\n\n")
        f.write(f"constexpr int k{prefix}WindowSamples = {window};\n")
        f.write(f"constexpr int k{prefix}NumAxes = {num_axes};\n")
        f.write(f"constexpr float k{prefix}AccelNormG = {ACCEL_NORM_G}f;\n")
        f.write(f"constexpr float k{prefix}GyroNormDps = {GYRO_NORM_DPS}f;\n\n")
        f.write(f"constexpr int k{prefix}NumClasses = {len(labels)};\n")
        f.write(f"const char* const k{prefix}Labels[k{prefix}NumClasses] = {{\n")
        f.write(",\n".join(f'  "{l}"' for l in labels))
        f.write("\n};\n\n")
        f.write(f"alignas(8) const unsigned char {model_var}[] = {{\n")
        for i in range(0, len(tflite_bytes), 12):
            chunk = tflite_bytes[i:i + 12]
            f.write("  " + ", ".join(f"0x{b:02x}" for b in chunk) + ",\n")
        f.write("};\n")
        f.write(f"const int {model_var}_len = {len(tflite_bytes)};\n")


def main():
    ap = argparse.ArgumentParser(description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter)
    ap.add_argument("--data", nargs="+", required=True, help="One or more capture_gesture_data.py CSV files")
    ap.add_argument("--group", required=True, choices=list(GROUP_CONFIG),
                     help="Which model to train: 'pose' (hub) or 'throttle' (fingers)")
    ap.add_argument("--window", type=int, default=WINDOW_SAMPLES, help=f"Window length in samples (default {WINDOW_SAMPLES})")
    ap.add_argument("--stride", type=int, default=None, help="Window stride in samples (default: window/2, 50%% overlap)")
    ap.add_argument("--epochs", type=int, default=30)
    ap.add_argument("--batch-size", type=int, default=32)
    ap.add_argument("--val-fraction", type=float, default=0.2, help="Fraction of reps (not windows) held out for validation")
    ap.add_argument("--seed", type=int, default=0)
    ap.add_argument("--out-tflite", default=None, help="Defaults to gesture_model.tflite / throttle_model.tflite depending on --group")
    ap.add_argument("--out-header", default=None, help="Defaults to gesture_model.h / throttle_model.h depending on --group")
    args = ap.parse_args()

    cfg = GROUP_CONFIG[args.group]
    labels = cfg["labels"]
    columns = cfg["columns"]
    num_axes = len(columns)
    out_tflite = args.out_tflite or cfg["default_tflite"]
    out_header = args.out_header or cfg["default_header"]

    window = args.window
    stride = args.stride if args.stride is not None else max(1, window // 2)

    reps = load_reps(args.data, args.group, columns)
    print(f"Loaded {len(reps)} '{args.group}' rep(s) from {len(args.data)} file(s).")
    per_label_reps = defaultdict(int)
    for (label, _rep_id) in reps:
        per_label_reps[label] += 1
    for label in labels:
        n = per_label_reps.get(label, 0)
        flag = "  <-- none recorded!" if n == 0 else ""
        print(f"  {label:<12s}: {n} rep(s){flag}")

    X, y, groups = make_windows(reps, labels, num_axes, window, stride)
    X = normalize(X)
    print(f"\nBuilt {len(X)} training window(s) from {len(set(groups))} rep(s).")

    train_mask, val_mask = group_split(groups, y, args.val_fraction, args.seed)
    X_train, y_train = X[train_mask], y[train_mask]
    X_val, y_val = X[val_mask], y[val_mask]
    print(f"Train: {len(X_train)} windows from {len(set(groups[train_mask]))} reps | "
          f"Val: {len(X_val)} windows from {len(set(groups[val_mask]))} reps")

    if len(X_val) == 0:
        print("No validation windows - can't reliably assess accuracy. Record more reps or "
              "lower --val-fraction cautiously.", file=sys.stderr)

    model = build_model(window, num_axes, len(labels))
    model.fit(
        X_train, y_train,
        validation_data=(X_val, y_val) if len(X_val) else None,
        epochs=args.epochs,
        batch_size=args.batch_size,
        verbose=2,
    )

    if len(X_val):
        val_pred = np.argmax(model.predict(X_val, verbose=0), axis=1)
        acc = float(np.mean(val_pred == y_val))
        print(f"\nValidation accuracy: {acc * 100:.1f}% ({len(X_val)} windows, "
              f"{len(set(groups[val_mask]))} held-out reps)")
        cm = confusion_matrix(y_val, val_pred, len(labels))
        print("\nConfusion matrix (rows = true label, columns = predicted):")
        print_confusion_matrix(cm, labels)

    converter = tf.lite.TFLiteConverter.from_keras_model(model)
    tflite_model = converter.convert()
    with open(out_tflite, "wb") as f:
        f.write(tflite_model)
    write_c_header(tflite_model, out_header, labels, window, num_axes, cfg["prefix"], cfg["model_var"])

    print(f"\nWrote {out_tflite} ({len(tflite_model)} bytes) and {out_header}.")
    print(f"Next: copy {out_header} into glove/src/gesture_classifier/ and flash gesture_classifier.ino.")


if __name__ == "__main__":
    main()
