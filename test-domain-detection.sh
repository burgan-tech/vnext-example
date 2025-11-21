#!/bin/bash

echo "Testing domain directory detection logic..."

# Find domain directory (could be template placeholder or actual domain name)
DOMAIN_DIRS=$(find . -maxdepth 1 -type d -name "*" | grep -v "^\.$" | grep -v "^\./\." | grep -v "node_modules" | grep -v "\.git")

DOMAIN_DIR=""
for dir in $DOMAIN_DIRS; do
  # Remove leading ./
  clean_dir=$(echo "$dir" | sed 's|^\./||')
  # Skip if it's a hidden directory, node_modules, or .git
  if [[ "$clean_dir" != .* && "$clean_dir" != "node_modules" && "$clean_dir" != ".git" ]]; then
    # Check if it contains vnext structure (Schemas, Workflows, etc.)
    if [ -d "$dir/Schemas" ] || [ -d "$dir/Workflows" ] || [ -d "$dir/Tasks" ]; then
      DOMAIN_DIR="$clean_dir"
      break
    fi
  fi
done

if [ -z "$DOMAIN_DIR" ]; then
  echo "⚠️  No domain directory found with vnext structure"
  echo "This might be a template repository - checking for template structure..."
  
  # Check if we have template structure
  if [ -d "core" ]; then
    DOMAIN_DIR="core"
    echo "✅ Found template domain directory: $DOMAIN_DIR"
  else
    echo "❌ No domain directory or template found"
    exit 1
  fi
else
  echo "✅ Found domain directory: $DOMAIN_DIR"
fi

echo "Final domain directory: $DOMAIN_DIR"

# Test JSON validation
echo "Testing JSON files in: $DOMAIN_DIR"
JSON_COUNT=0
if [ -d "$DOMAIN_DIR" ]; then
  while IFS= read -r -d '' json_file; do
    echo "Found JSON file: $json_file"
    if ! jq empty "$json_file" 2>/dev/null; then
      echo "❌ Invalid JSON in $json_file"
    else
      echo "✅ Valid JSON: $json_file"
      JSON_COUNT=$((JSON_COUNT + 1))
    fi
  done < <(find "$DOMAIN_DIR" -name "*.json" -type f -print0)
fi

echo "Total JSON files validated: $JSON_COUNT"
