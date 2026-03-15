import argparse
import csv
import os
import re
import sqlite3
from datetime import datetime

MONTHS_SET = {
    "January", "February", "March", "April", "May", "June",
    "July", "August", "September", "October", "November", "December",
}
DETAIL_HEADER = ["name", "category", "amount", "due date", "status", "recurring", "past due", "month", "year"]


def parse_amount(value: str):
    value = (value or "").strip()
    if not value:
        return None
    try:
        return float(value)
    except ValueError:
        try:
            return float(value.replace(",", ""))
        except ValueError:
            return None


def parse_due_date(value: str):
    value = (value or "").strip()
    if not value:
        return None
    for fmt in ("%m/%d/%Y", "%Y-%m-%d", "%Y-%m-%d %H:%M:%S"):
        try:
            return datetime.strptime(value, fmt).strftime("%Y-%m-%d")
        except ValueError:
            pass
    return None


def derive_tag_from_csv(csv_path: str):
    match = re.search(r"auto_export_(\d{8}_\d{6})\.csv$", os.path.basename(csv_path), re.IGNORECASE)
    if match:
        return match.group(1)
    return datetime.now().strftime("%Y%m%d_%H%M%S")


def parse_csv_rows(csv_path: str):
    rows = []
    current_year = None
    current_month = None
    collecting = False

    with open(csv_path, newline="", encoding="utf-8") as handle:
        for raw in handle:
            line = raw.rstrip("\n")
            if not line.strip():
                collecting = False
                continue

            parts = next(csv.reader([line]))
            if not parts:
                collecting = False
                continue

            first = parts[0].strip()

            if len(parts) >= 2 and first.startswith("===== YEAR"):
                current_year = parts[1].strip()
                collecting = False
                continue

            if len(parts) >= 2 and first.startswith("--- MONTH"):
                current_month = parts[1].strip()
                collecting = False
                continue

            if first.startswith("===== NOTES"):
                collecting = False
                continue

            if first.upper().endswith("SUMMARY"):
                collecting = False
                continue

            header = [p.strip().lower() for p in parts]
            if header[: len(DETAIL_HEADER)] == DETAIL_HEADER:
                collecting = True
                continue

            if not collecting or len(parts) < 9:
                continue

            month_col = parts[7].strip()
            year_col = parts[8].strip()
            col_month = month_col or current_month
            col_year = year_col or current_year

            if col_month not in MONTHS_SET:
                continue
            if not col_year or not str(col_year).strip().isdigit():
                continue

            rows.append(
                {
                    "name": parts[0].strip(),
                    "category": parts[1].strip(),
                    "amount": parse_amount(parts[2]),
                    "due": parse_due_date(parts[3]),
                    "month": col_month,
                    "year": str(col_year),
                    "raw": parts,
                }
            )

    return rows


def row_exists(cur: sqlite3.Cursor, row: dict):
    if row["amount"] is None:
        return False

    sql = """
    SELECT 1
    FROM Bills
    WHERE Name = ?
      AND Month = ?
      AND CAST(Year AS TEXT) = ?
      AND ABS(CAST(Amount AS REAL) - ?) < 0.001
      AND (
            (? IS NULL AND (DueDate IS NULL OR TRIM(DueDate) = ''))
         OR (? IS NOT NULL AND date(DueDate) = date(?))
      )
      AND (? = '' OR Category = ?)
    LIMIT 1
    """
    cur.execute(
        sql,
        (
            row["name"],
            row["month"],
            row["year"],
            float(row["amount"]),
            row["due"],
            row["due"],
            row["due"],
            row["category"] or "",
            row["category"] or "",
        ),
    )
    return cur.fetchone() is not None


def default_data_root() -> str:
    home = os.path.expanduser("~")
    docs = os.path.join(home, "Documents")
    return os.path.join(docs, "CrispyBills")


def main():
    root = default_data_root()

    parser = argparse.ArgumentParser(description="Compare exported CSV rows against test DB rows.")
    parser.add_argument("--root", default=root, help="Base CrispyBills data directory (default: ~/Documents/CrispyBills)")
    parser.add_argument("--csv-path", default="", help="Path to auto_export_*.csv (defaults to latest under --root)")
    parser.add_argument("--test-db-dir", default="", help="Directory containing CrispyBills_<year>_test.db files (defaults to --root/db_backups/auto_tests/test_dbs)")
    args = parser.parse_args()

    root = args.root
    csv_path = args.csv_path
    test_db_dir = args.test_db_dir or os.path.join(root, "db_backups", "auto_tests", "test_dbs")
    if not os.path.exists(csv_path):
        auto_test_dir = os.path.join(root, "db_backups", "auto_tests")
        candidates = []
        if os.path.isdir(auto_test_dir):
            for name in os.listdir(auto_test_dir):
                if re.match(r"auto_export_\d{8}_\d{6}\.csv$", name, re.IGNORECASE):
                    full = os.path.join(auto_test_dir, name)
                    candidates.append((os.path.getmtime(full), full))

        if candidates:
            candidates.sort(reverse=True)
            csv_path = candidates[0][1]
            print(f"Using latest export CSV: {csv_path}")
        else:
            raise FileNotFoundError(f"CSV not found: {args.csv_path}")

    os.makedirs(test_db_dir, exist_ok=True)
    tag = derive_tag_from_csv(csv_path)
    output_path = os.path.join(test_db_dir, f"per_row_diff_{tag}.txt")
    preview_path = os.path.join(test_db_dir, f"parsed_rows_preview_{tag}.txt")

    rows = parse_csv_rows(csv_path)
    missing = []

    connections = {}
    cursors = {}
    try:
        for row in rows:
            year = row["year"]
            db_file = os.path.join(test_db_dir, f"CrispyBills_{year}_test.db")
            if not os.path.exists(db_file):
                missing.append((row, f"DB missing: {db_file}"))
                continue

            if year not in connections:
                connections[year] = sqlite3.connect(db_file)
                cursors[year] = connections[year].cursor()

            if not row_exists(cursors[year], row):
                missing.append((row, "Not found in DB"))
    finally:
        for conn in connections.values():
            conn.close()

    print(f"DEBUG: parsed_rows={len(rows)}, missing={len(missing)}")
    with open(output_path, "w", encoding="utf-8") as out:
        out.write(f"Per-row diff report for {os.path.basename(csv_path)}\\n")
        out.write(f"Total CSV detail rows: {len(rows)}\\n")
        out.write(f"Total missing in DB: {len(missing)}\\n\\n")
        for row, reason in missing:
            out.write(
                f"Missing: {row['year']} - {row['month']} - {row['name']} | "
                f"Category: {row['category']} | Amount: {row['amount']} | Due: {row['due']} | Reason: {reason}\\n"
            )

    print("Done. Report written to", output_path)

    with open(preview_path, "w", encoding="utf-8") as preview:
        preview.write(f"Parsed detail rows (first 60) from {csv_path}\\n")
        preview.write(f"Total parsed rows: {len(rows)}\\n\\n")
        for i, row in enumerate(rows[:60], start=1):
            preview.write(
                f"#{i}: year={row['year']}, month={row['month']}, name={row['name']}, "
                f"category={row['category']}, amount={row['amount']}, due={row['due']}, raw={row['raw']}\\n"
            )

    print("Preview written to", preview_path)


if __name__ == "__main__":
    main()
