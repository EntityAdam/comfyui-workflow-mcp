#!/usr/bin/env bash
set -euo pipefail

repository_root="$(cd "$(dirname "${BASH_SOURCE[0]}")/.." && pwd)"
cd "$repository_root"

git diff --check

ruby <<'RUBY'
files = ["README.md"] + Dir["docs/**/*.md"]
missing = []

files.each do |file|
  File.read(file).scan(/\[[^\]]+\]\(([^)]+)\)/).flatten.each do |target|
    next if target.match?(%r{^(https?://|#)})

    path = target.split("#", 2).first
    resolved = File.expand_path(path, File.dirname(file))
    missing << "#{file}: #{target}" unless File.exist?(resolved)
  end
end

abort "Missing local links:\n#{missing.join("\n")}" unless missing.empty?
puts "Validated local Markdown links in #{files.length} files."
RUBY
