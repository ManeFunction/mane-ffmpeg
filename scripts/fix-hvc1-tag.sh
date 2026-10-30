#!/usr/bin/env bash
# Finds HEVC .mp4 files under a folder whose video isn't tagged hvc1 (usually ffmpeg's default hev1, which
# QuickTime and Finder preview refuse) and remuxes them with the hvc1 tag. Every stream is copied unchanged.
# The original goes to the Trash only after the new file checks out; otherwise the error is printed and the file kept.
#
# usage: fix-hvc1-tag.sh [-n] [-p] <folder>
#   -n  only list the files that would be fixed
#   -p  delete the originals permanently instead of moving them to the Trash
set -u

usage() { echo "usage: ${0##*/} [-n] [-p] <folder>" >&2; exit 2; }
dry_run=0 permanent=0
while getopts "np" opt; do
  case $opt in
    n) dry_run=1 ;;
    p) permanent=1 ;;
    *) usage ;;
  esac
done
shift $((OPTIND - 1))
if [ $# -ne 1 ] || [ ! -d "$1" ]; then usage; fi
for tool in ffmpeg ffprobe; do
  command -v "$tool" >/dev/null || { echo "error: $tool not found" >&2; exit 1; }
done

tmp=""
trap '[ -n "$tmp" ] && rm -f "$tmp"; echo; echo "interrupted" >&2; exit 130' INT TERM

# "codec,tag" of the first video stream, e.g. "hevc,hev1".
video_tag() { ffprobe -v error -select_streams v:0 -show_entries stream=codec_name,codec_tag_string -of csv=p=0 "$1" 2>/dev/null | head -n 1; }
stream_count() { ffprobe -v error -show_entries stream=index -of csv=p=0 "$1" 2>/dev/null | wc -l | tr -d ' '; }
duration() { ffprobe -v error -show_entries format=duration -of default=nw=1:nk=1 "$1" 2>/dev/null; }

# Moves a file to the Trash, where Finder can put it back. macOS 14 and later have a trash command; older
# systems ask Finder (the first time, macOS asks to let the terminal control Finder).
to_trash() {
  if command -v trash >/dev/null; then
    trash "$1"
  else
    osascript -e 'on run argv' -e 'tell application "Finder" to delete (POSIX file (item 1 of argv) as alias)' \
      -e 'end run' "$1" >/dev/null
  fi
}

fixed=0 failed=0 skipped=0
# The temporary files end in .tmp, so find never picks them up while the loop runs.
while IFS= read -r -d '' f; do
  case "$(video_tag "$f")" in
    hevc,hvc1|"") skipped=$((skipped + 1)); continue ;;  # already tagged, or not readable as video
    hevc,*) ;;
    *) skipped=$((skipped + 1)); continue ;;             # not HEVC
  esac

  if [ $dry_run -eq 1 ]; then
    echo "would fix: $f"
    fixed=$((fixed + 1))
    continue
  fi

  echo "fixing: $f"
  tmp="$f.hvc1.tmp"
  if ! log=$(ffmpeg -nostdin -v error -y -i "$f" -map 0 -c copy -tag:v hvc1 -movflags +faststart -f mp4 "$tmp" 2>&1); then
    echo "  error: ffmpeg failed: ${log:-unknown error}" >&2
    rm -f "$tmp"; tmp=""; failed=$((failed + 1)); continue
  fi

  # The copy must have the new tag, the same streams and the same length before it replaces the original.
  problem=""
  if [ "$(video_tag "$tmp")" != "hevc,hvc1" ]; then
    problem="the new file isn't tagged hvc1"
  elif [ "$(stream_count "$tmp")" != "$(stream_count "$f")" ]; then
    problem="the new file has $(stream_count "$tmp") streams instead of $(stream_count "$f")"
  elif ! awk -v a="$(duration "$f")" -v b="$(duration "$tmp")" \
      'BEGIN { d = a - b; if (d < 0) d = -d; exit !(a != "" && b != "" && d < 0.5) }'; then
    problem="the duration changed ($(duration "$f")s -> $(duration "$tmp")s)"
  fi
  if [ -n "$problem" ]; then
    echo "  error: $problem; original kept" >&2
    rm -f "$tmp"; tmp=""; failed=$((failed + 1)); continue
  fi

  touch -r "$f" "$tmp"  # keep the original's modification date
  if [ $permanent -eq 1 ]; then
    if mv -f "$tmp" "$f"; then
      fixed=$((fixed + 1))
    else
      echo "  error: couldn't replace the original" >&2
      rm -f "$tmp"; failed=$((failed + 1))
    fi
  elif ! to_trash "$f"; then
    echo "  error: couldn't move the original to the Trash; original kept" >&2
    rm -f "$tmp"; failed=$((failed + 1))
  elif mv "$tmp" "$f"; then
    fixed=$((fixed + 1))
  else
    # The original is already in the Trash, so the new file must stay where it is.
    echo "  error: the original is in the Trash, but the new file couldn't take its name: it's at $tmp" >&2
    failed=$((failed + 1))
  fi
  tmp=""
done < <(find "$1" -type f -iname '*.mp4' -print0)

if [ $dry_run -eq 1 ]; then
  echo "done: $fixed to fix, $skipped skipped"
else
  echo "done: $fixed fixed, $failed failed, $skipped skipped"
fi
[ $failed -eq 0 ]
