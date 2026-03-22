"""
verify_live_dbs.py

Quick helper to inspect live per-year SQLite databases under the
user's Documents/CrispyBills folder and write a small diagnostics
report showing bill counts and income per month. Intended for manual
verification and local troubleshooting.

Adjust `root` or the `year` tuple below when running against different
targets.
"""

import sqlite3
import os
from datetime import datetime

# Base path where per-year DB files are expected (user Documents/CrispyBills)
root = r"C:\Users\Chris\Documents\CrispyBills"

# Human-friendly month names used in on-disk exports and reports
months = [
    "January", "February", "March", "April", "May", "June",
    "July", "August", "September", "October", "November", "December"
]

report_lines = []
now = datetime.now().strftime('%Y%m%d_%H%M%S')
report_path = os.path.join(root, "db_backups", "import_diagnostics", f"live_db_verify_{now}.txt")
os.makedirs(os.path.dirname(report_path), exist_ok=True)

# Years to inspect - update as needed for your environment
for year in ("2026", "2027"):
    db = os.path.join(root, f"CrispyBills_{year}.db")
    report_lines.append(f"Year: {year} | DB: {db}\n")
    if not os.path.exists(db):
        report_lines.append("  DB missing\n\n")
        continue
    conn = sqlite3.connect(db)
    cur = conn.cursor()
    total = 0
    for m in months:
        cur.execute("SELECT COUNT(*) FROM Bills WHERE Year = ? AND Month = ?", (year, m))
        cnt = cur.fetchone()[0]
        cur.execute("SELECT Amount FROM Income WHERE Year = ? AND Month = ?", (year, m))
        row = cur.fetchone()
        income = row[0] if row is not None else 0.0
        report_lines.append(f"  {m}: {cnt} bill(s), Income: {income:.2f}\n")
        total += cnt
    report_lines.append(f"  Total bills: {total}\n\n")
    conn.close()

with open(report_path, 'w', encoding='utf-8') as f:
    f.writelines(report_lines)

print('Wrote verification report to', report_path)
