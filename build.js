#!/usr/bin/env node

const fs = require('fs');
const path = require('path');

// ANSI color codes for terminal output
const colors = {
  reset: '\x1b[0m',
  red: '\x1b[31m',
  green: '\x1b[32m',
  yellow: '\x1b[33m',
  blue: '\x1b[34m',
  magenta: '\x1b[35m',
  cyan: '\x1b[36m',
  bright: '\x1b[1m',
  dim: '\x1b[2m'
};

function colorize(text, color) {
  return `${colors[color]}${text}${colors.reset}`;
}

// Load vnext.config.json
function loadConfig() {
  try {
    return JSON.parse(fs.readFileSync('vnext.config.json', 'utf8'));
  } catch (error) {
    return null;
  }
}

// Get paths configuration with defaults
function getPathsConfig() {
  const config = loadConfig();
  const defaults = {
    componentsRoot: 'core',
    schemas: 'Schemas',
    workflows: 'Workflows',
    tasks: 'Tasks',
    views: 'Views',
    functions: 'Functions',
    extensions: 'Extensions',
    mappings: 'Mappings'
  };
  
  if (config && config.paths) {
    return { ...defaults, ...config.paths };
  }
  return defaults;
}

// Recursively find all JSON files in a directory
function findJsonFiles(dirPath, files = []) {
  if (!fs.existsSync(dirPath)) {
    return files;
  }

  const entries = fs.readdirSync(dirPath, { withFileTypes: true });
  
  for (const entry of entries) {
    // Skip .meta directory
    if (entry.name === '.meta') {
      continue;
    }
    
    const fullPath = path.join(dirPath, entry.name);
    
    if (entry.isDirectory()) {
      findJsonFiles(fullPath, files);
    } else if (entry.isFile() && entry.name.endsWith('.json')) {
      files.push(fullPath);
    }
  }
  
  return files;
}

// Recursively get all files in a directory
function getAllFiles(dirPath, files = []) {
  if (!fs.existsSync(dirPath)) {
    return files;
  }

  const entries = fs.readdirSync(dirPath, { withFileTypes: true });
  
  for (const entry of entries) {
    // Skip .meta directory
    if (entry.name === '.meta') {
      continue;
    }
    
    const fullPath = path.join(dirPath, entry.name);
    
    if (entry.isDirectory()) {
      getAllFiles(fullPath, files);
    } else {
      files.push(fullPath);
    }
  }
  
  return files;
}

// Recursively remove directory
function removeDir(dirPath) {
  if (fs.existsSync(dirPath)) {
    fs.readdirSync(dirPath).forEach(file => {
      const curPath = path.join(dirPath, file);
      if (fs.lstatSync(curPath).isDirectory()) {
        removeDir(curPath);
      } else {
        fs.unlinkSync(curPath);
      }
    });
    fs.rmdirSync(dirPath);
  }
}

// Recursively create directory
function ensureDir(dirPath) {
  if (!fs.existsSync(dirPath)) {
    fs.mkdirSync(dirPath, { recursive: true });
  }
}

// Copy file with directory creation
function copyFile(src, dest) {
  ensureDir(path.dirname(dest));
  fs.copyFileSync(src, dest);
}

// Write JSON file with directory creation
function writeJsonFile(filePath, data) {
  ensureDir(path.dirname(filePath));
  fs.writeFileSync(filePath, JSON.stringify(data, null, 2), 'utf8');
}

// Parse command line arguments
function parseArgs() {
  const args = process.argv.slice(2);
  const options = {
    output: 'dist',
    type: 'runtime', // default to runtime
    skipValidation: false,
    help: false
  };

  for (let i = 0; i < args.length; i++) {
    const arg = args[i];
    
    if (arg === '-o' || arg === '--output') {
      options.output = args[++i];
    } else if (arg === '-t' || arg === '--type') {
      options.type = args[++i];
    } else if (arg === '--skip-validation') {
      options.skipValidation = true;
    } else if (arg === '-h' || arg === '--help') {
      options.help = true;
    }
  }

  return options;
}

// Show help
function showHelp() {
  console.log(`
${colorize('vNext Domain Build Tool', 'bright')}

${colorize('Usage:', 'yellow')}
  node build.js [options]
  npm run build -- [options]

${colorize('Options:', 'yellow')}
  -o, --output <dir>     Output directory (default: dist)
  -t, --type <type>      Build type: reference or runtime (default: runtime)
  --skip-validation      Skip validation during build
  -h, --help             Show this help message

${colorize('Build Types:', 'yellow')}
  ${colorize('reference', 'cyan')}  - Exports only (for cross-domain usage)
                   Only components listed in vnext.config.json exports are included
  
  ${colorize('runtime', 'cyan')}    - Complete domain structure (for engine deployment)
                   All components and supporting files are included

${colorize('Examples:', 'yellow')}
  npm run build                          # Runtime build to dist/
  npm run build -- -t reference          # Reference build to dist/
  npm run build -- -o build -t runtime   # Runtime build to build/
  npm run build -- --skip-validation     # Skip validation step
`);
}

// Main build function
async function build() {
  const options = parseArgs();

  if (options.help) {
    showHelp();
    process.exit(0);
  }

  // Validate build type
  if (!['reference', 'runtime'].includes(options.type)) {
    console.log(colorize('❌ Invalid build type. Use "reference" or "runtime".', 'red'));
    process.exit(1);
  }

  console.log(colorize(`\n🏗️  Building ${options.type} package...`, 'blue'));
  console.log('═'.repeat(60));

  // Load configuration
  const config = loadConfig();
  if (!config) {
    console.log(colorize('❌ vnext.config.json not found.', 'red'));
    process.exit(1);
  }

  const pathsConfig = getPathsConfig();
  const domainPath = pathsConfig.componentsRoot;
  const outputDir = options.output;

  console.log(`   Domain: ${colorize(config.domain, 'cyan')}`);
  console.log(`   Build Type: ${colorize(options.type, 'magenta')}`);
  console.log(`   Output: ${colorize(outputDir, 'yellow')}`);

  // Step 1: Validation (unless skipped)
  if (!options.skipValidation) {
    console.log(colorize('\n📋 Step 1: Running full validation (validate.js)...', 'blue'));
    console.log('─'.repeat(60));
    
    try {
      const { execSync } = require('child_process');
      execSync('node validate.js', { stdio: 'inherit' });
      console.log(colorize('   ✅ Validation completed successfully', 'green'));
    } catch (error) {
      console.log(colorize('\n❌ Build failed: Validation errors found', 'red'));
      console.log(colorize('   Run "npm run validate" for detailed error information', 'dim'));
      process.exit(1);
    }
  } else {
    console.log(colorize('\n⚠️  Step 1: Validation skipped (--skip-validation)', 'yellow'));
  }

  // Step 2: Clean and prepare output directory
  console.log(colorize('\n📦 Step 2: Preparing build directory...', 'blue'));
  console.log('─'.repeat(60));
  
  removeDir(outputDir);
  ensureDir(outputDir);
  console.log(colorize(`   ✅ Output directory prepared: ${outputDir}`, 'green'));

  // Step 3: Copy configuration files
  console.log(colorize('\n📄 Step 3: Copying configuration files...', 'blue'));
  console.log('─'.repeat(60));

  // Copy vnext.config.json
  writeJsonFile(path.join(outputDir, 'vnext.config.json'), config);
  console.log(colorize('   ✅ vnext.config.json', 'green'));

  // Copy and modify package.json
  if (fs.existsSync('package.json')) {
    const packageJson = JSON.parse(fs.readFileSync('package.json', 'utf8'));
    const originalPackageName = packageJson.name;

    // Modify package name based on build type
    if (options.type === 'reference') {
      packageJson.name = `${originalPackageName}-reference`;
      packageJson.description = `${packageJson.description || ''} (Reference Package for Cross-Domain Usage)`.trim();
    } else if (options.type === 'runtime') {
      packageJson.name = `${originalPackageName}-runtime`;
      packageJson.description = `${packageJson.description || ''} (Runtime Package for Engine Deployment)`.trim();
    }

    // Add build type metadata
    packageJson.vnext = {
      ...packageJson.vnext,
      buildType: options.type,
      buildTimestamp: new Date().toISOString(),
      originalPackage: originalPackageName
    };

    // Remove scripts that aren't needed in the built package
    delete packageJson.scripts;
    delete packageJson.devDependencies;
    delete packageJson.bin;

    writeJsonFile(path.join(outputDir, 'package.json'), packageJson);
    console.log(colorize(`   ✅ package.json (${packageJson.name})`, 'green'));
  }

  // Copy README.md if exists
  if (fs.existsSync('README.md')) {
    copyFile('README.md', path.join(outputDir, 'README.md'));
    console.log(colorize('   ✅ README.md', 'green'));
  }

  // Copy LICENSE if exists
  if (fs.existsSync('LICENSE')) {
    copyFile('LICENSE', path.join(outputDir, 'LICENSE'));
    console.log(colorize('   ✅ LICENSE', 'green'));
  }

  // Step 4: Process and copy components based on build type
  let copiedFiles = 0;
  let copiedSupportFiles = 0;

  if (options.type === 'reference') {
    console.log(colorize('\n🔗 Step 4: Processing exported components...', 'blue'));
    console.log('─'.repeat(60));

    // Create domain root and component directories from paths config
    const targetDomainPath = path.join(outputDir, config.domain);
    ensureDir(targetDomainPath);
    
    const componentDirs = [
      pathsConfig.schemas,
      pathsConfig.workflows,
      pathsConfig.tasks,
      pathsConfig.views,
      pathsConfig.functions,
      pathsConfig.extensions,
      pathsConfig.mappings
    ];
    
    for (const dir of componentDirs) {
      const targetDir = path.join(targetDomainPath, dir);
      ensureDir(targetDir);
    }
    console.log(colorize(`   ✅ Created domain structure: ${config.domain}/`, 'green'));

    // Check if there are any exports configured
    const hasExports = config.exports && Object.entries(config.exports).some(
      ([key, files]) => Array.isArray(files) && files.length > 0
    );

    if (hasExports) {
      for (const [category, files] of Object.entries(config.exports)) {
        if (Array.isArray(files) && files.length > 0) {
          const categoryPath = pathsConfig[category] || category;

          for (const filename of files) {
            const sourcePath = path.join(domainPath, categoryPath, filename);
            const targetPath = path.join(outputDir, config.domain, categoryPath, filename);

            if (fs.existsSync(sourcePath)) {
              try {
                const content = JSON.parse(fs.readFileSync(sourcePath, 'utf8'));
                writeJsonFile(targetPath, content);
                console.log(colorize(`   ✅ ${categoryPath}/${filename}`, 'green'));
                copiedFiles++;
              } catch (error) {
                console.log(colorize(`   ❌ ${categoryPath}/${filename}: ${error.message}`, 'red'));
              }
            } else {
              console.log(colorize(`   ⚠️  Not found: ${categoryPath}/${filename}`, 'yellow'));
            }
          }
        }
      }
    } else {
      console.log(colorize('   ℹ️  No exports configured - empty domain structure created', 'dim'));
    }

  } else if (options.type === 'runtime') {
    console.log(colorize('\n📁 Step 4: Processing complete domain structure...', 'blue'));
    console.log('─'.repeat(60));

    if (fs.existsSync(domainPath)) {
      const allFiles = getAllFiles(domainPath);
      const targetDomainPath = path.join(outputDir, config.domain);

      for (const filePath of allFiles) {
        const relativePath = path.relative(domainPath, filePath);
        const targetPath = path.join(targetDomainPath, relativePath);

        try {
          if (path.extname(filePath) === '.json') {
            // Process JSON files
            const content = JSON.parse(fs.readFileSync(filePath, 'utf8'));
            writeJsonFile(targetPath, content);
            copiedFiles++;
          } else {
            // Copy non-JSON files as-is
            copyFile(filePath, targetPath);
            copiedSupportFiles++;
          }
        } catch (error) {
          console.log(colorize(`   ⚠️  Error processing ${relativePath}: ${error.message}`, 'yellow'));
          // Copy as-is if processing fails
          copyFile(filePath, targetPath);
        }
      }

      console.log(colorize(`   ✅ Processed ${copiedFiles} JSON component files`, 'green'));
      console.log(colorize(`   ✅ Copied ${copiedSupportFiles} supporting files`, 'green'));
    } else {
      console.log(colorize(`   ❌ Domain directory not found: ${domainPath}`, 'red'));
      process.exit(1);
    }
  }

  // Build Summary
  console.log('\n' + '═'.repeat(60));
  console.log(colorize('📊 Build Summary:', 'bright'));
  console.log('═'.repeat(60));
  console.log(`   Build type: ${colorize(options.type, 'magenta')}`);
  console.log(`   JSON files processed: ${colorize(copiedFiles, 'cyan')}`);
  if (options.type === 'runtime') {
    console.log(`   Supporting files copied: ${colorize(copiedSupportFiles, 'cyan')}`);
  }
  console.log(`   Output directory: ${colorize(outputDir, 'yellow')}`);

  if (options.type === 'reference') {
    console.log(colorize('\n   Package contents: Exported components only (for cross-domain usage)', 'dim'));
  } else {
    console.log(colorize('\n   Package contents: Complete domain structure (for runtime deployment)', 'dim'));
  }

  console.log('═'.repeat(60));
  console.log(colorize(`\n🎉 ${options.type.charAt(0).toUpperCase() + options.type.slice(1)} package built successfully!`, 'green'));
  console.log(colorize(`   Output: ${path.resolve(outputDir)}\n`, 'dim'));
}

// Run build
build().catch(error => {
  console.error(colorize(`\n❌ Build error: ${error.message}`, 'red'));
  process.exit(1);
});

