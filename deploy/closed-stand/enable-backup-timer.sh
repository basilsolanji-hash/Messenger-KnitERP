#!/usr/bin/env bash
# Install and enable the daily backup timer. Run only AFTER restore-test.sh succeeded (step 8).
set -euo pipefail
ROOT=/opt/os-communications
[ -s /etc/os-communications/backup.env ] || { echo 'Run backup-now.sh first.' >&2; exit 1; }
for unit in os-communications-backup.service os-communications-backup.timer; do
    install -m 0644 "$ROOT/deploy/systemd/$unit.example" "/etc/systemd/system/$unit"
done
systemd-analyze verify /etc/systemd/system/os-communications-backup.service /etc/systemd/system/os-communications-backup.timer 2>&1 | grep -v xfs_scrub || true
systemctl daemon-reload
systemctl enable --now os-communications-backup.timer
systemctl list-timers os-communications-backup.timer --no-pager
