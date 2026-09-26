#!/usr/bin/env bash
# Drives Steps 6-8 against running services. Usage: scripts/demo.sh [happy|compensate|burst|check|all]
set -euo pipefail

API="${API:-http://localhost:5080}"
SECTION="${SECTION:-11111111-1111-1111-1111-111111111111}"   # seeded CS101
SA_PASSWORD="${SA_PASSWORD:-Your_password123}"
HERE="$(cd "$(dirname "$0")" && pwd)"

sql() {  # runs a .sql file inside the sqlserver container
  docker exec -i sqlserver /opt/mssql-tools18/bin/sqlcmd -C -S localhost -U sa -P "$SA_PASSWORD" -W < "$1"
}

enroll() {  # enroll <studentId> <tuition>
  curl -s -X POST "$API/enrollments" -H "Content-Type: application/json" \
    -d "{\"studentId\":\"$1\",\"sectionId\":\"$SECTION\",\"tuition\":$2}"
  echo
}

state() { curl -s "$API/state"; echo; }

happy() {
  echo "== Step 6: happy path (tuition 15000 <= limit)"
  enroll 2026-00123 15000
  sleep 1; state
}

compensate() {
  echo "== Step 7: compensation path (tuition 50000 > limit)"
  enroll 2026-00124 50000
  sleep 1; state
}

burst() {
  echo "== Bonus: 60 concurrent requests for a 30-seat section, half of them declined"
  for i in $(seq 1 60); do
    t=$(( i % 2 == 0 ? 10000 : 30000 ))
    curl -s -o /dev/null -w "%{http_code}\n" -X POST "$API/enrollments" -H "Content-Type: application/json" \
      -d "{\"studentId\":\"burst-$i\",\"sectionId\":\"$SECTION\",\"tuition\":$t}" &
  done | sort | uniq -c | sed 's/^/   HTTP /'
  wait; sleep 3
}

check() {
  echo "== Step 8: both databases, side by side"
  sql "$HERE/consistency.sql"
}

case "${1:-all}" in
  happy) happy ;;
  compensate) compensate ;;
  burst) burst ;;
  check) check ;;
  all) happy; compensate; check ;;
  *) echo "usage: $0 [happy|compensate|burst|check|all]"; exit 1 ;;
esac
