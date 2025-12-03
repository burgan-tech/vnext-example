#!/usr/bin/env node

const fs = require('fs');
const path = require('path');
const readline = require('readline');

// Load configuration from vnext.config.json
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
    extensions: 'Extensions'
  };
  
  if (config && config.paths) {
    return { ...defaults, ...config.paths };
  }
  return defaults;
}

// Check if already set up (componentsRoot directory exists with vnext structure)
function isAlreadySetup() {
  const pathsConfig = getPathsConfig();
  const componentsRoot = pathsConfig.componentsRoot;
  
  // Check if componentsRoot directory exists and contains vnext structure
  if (componentsRoot && fs.existsSync(componentsRoot)) {
    const schemasPath = path.join(componentsRoot, pathsConfig.schemas);
    const workflowsPath = path.join(componentsRoot, pathsConfig.workflows);
    const tasksPath = path.join(componentsRoot, pathsConfig.tasks);
    
    if (fs.existsSync(schemasPath) ||
        fs.existsSync(workflowsPath) ||
        fs.existsSync(tasksPath)) {
      return true;
    }
  }
  
  return false;
}

// Validate domain name
function validateDomainName(domainName) {
  if (!domainName) {
    console.error('❌ Domain name cannot be empty');
    return false;
  }
  // Validate domain name format (alphanumeric, hyphens, underscores)
  if (!/^[a-zA-Z0-9_-]+$/.test(domainName)) {
    console.error('❌ Domain name can only contain letters, numbers, hyphens, and underscores');
    return false;
  }
  return true;
}

// Prompt for domain name
function promptDomainName() {
  const rl = readline.createInterface({
    input: process.stdin,
    output: process.stdout
  });

  return new Promise((resolve) => {
    rl.question('Enter your domain name (e.g., "user-management", "order-processing"): ', (answer) => {
      rl.close();
      const domainName = answer.trim();
      if (!validateDomainName(domainName)) {
        process.exit(1);
      }
      resolve(domainName);
    });
  });
}

// Get domain name from command line arguments, environment variable, or prompt
async function getDomainName() {
  // Check for command line argument
  const args = process.argv.slice(2);
  if (args.length > 0) {
    const domainName = args[0].trim();
    if (validateDomainName(domainName)) {
      return domainName;
    } else {
      process.exit(1);
    }
  }
  
  // Check for environment variable (for npm install usage)
  if (process.env.DOMAIN_NAME) {
    const domainName = process.env.DOMAIN_NAME.trim();
    if (validateDomainName(domainName)) {
      return domainName;
    } else {
      console.error(`❌ Invalid domain name in DOMAIN_NAME environment variable: $core`);
      process.exit(1);
    }
  }
  
  // Otherwise prompt for it
  return await promptDomainName();
}

// Replace core in a file
function replaceInFile(filePath, domainName) {
  try {
    let content = fs.readFileSync(filePath, 'utf8');
    const originalContent = content;
    content = content.replace(/\{domainName\}/g, domainName);
    
    if (content !== originalContent) {
      fs.writeFileSync(filePath, content, 'utf8');
      return true;
    }
    return false;
  } catch (error) {
    console.warn(`⚠️  Warning: Could not process ${filePath}: ${error.message}`);
    return false;
  }
}

// Replace core in all files recursively
function replaceInDirectory(dirPath, domainName, processedFiles = new Set()) {
  if (!fs.existsSync(dirPath)) {
    return;
  }

  const entries = fs.readdirSync(dirPath, { withFileTypes: true });
  
  for (const entry of entries) {
    const fullPath = path.join(dirPath, entry.name);
    const relativePath = path.relative('.', fullPath);
    
    // Skip node_modules, .git, and other common directories
    if (entry.name === 'node_modules' || 
        entry.name === '.git' || 
        entry.name === 'dist' ||
        entry.name === '.github' ||
        entry.name.startsWith('.')) {
      continue;
    }

    if (entry.isDirectory()) {
      replaceInDirectory(fullPath, domainName, processedFiles);
    } else if (entry.isFile()) {
      // Process text files
      const ext = path.extname(entry.name).toLowerCase();
      if (['.json', '.js', '.md', '.sh', '.txt', '.yml', '.yaml'].includes(ext) ||
          entry.name === '.gitignore' ||
          entry.name === '.gitattributes') {
        if (!processedFiles.has(relativePath)) {
          if (replaceInFile(fullPath, domainName)) {
            processedFiles.add(relativePath);
          }
        }
      }
    }
  }
}

// Rename core directory
function renameDomainDirectory(domainName) {
  const templateDir = 'core';
  const targetDir = domainName;
  
  if (fs.existsSync(templateDir)) {
    if (fs.existsSync(targetDir)) {
      console.error(`❌ Error: Directory ${targetDir} already exists`);
      process.exit(1);
    }
    fs.renameSync(templateDir, targetDir);
    console.log(`  ✓ Renamed ${templateDir}/ to ${targetDir}/`);
    return true;
  }
  return false;
}

// Check if running in node_modules (installed as dependency)
function isInstalledAsDependency() {
  const cwd = process.cwd();
  return cwd.includes('node_modules');
}

// Main setup function
async function setup() {
  // Skip if installed as a dependency
  if (isInstalledAsDependency()) {
    return;
  }

  // Check if already set up first
  if (isAlreadySetup()) {
    return;
  }

  // Check if core directory exists (template not yet configured)
  if (!fs.existsSync('core')) {
    console.log('⚠️  Template directory core not found');
    console.log('   This might already be a configured project.\n');
    return;
  }

  console.log('🚀 vNext Template Setup');
  console.log('=======================\n');

  // Get domain name from command line or prompt
  const domainName = await getDomainName();
  console.log(`\n📝 Setting up domain: $core\n`);

  // Replace in all files
  console.log('🔄 Replacing core in files...');
  const processedFiles = new Set();
  replaceInDirectory('.', domainName, processedFiles);
  console.log(`  ✓ Processed ${processedFiles.size} file(s)`);

  // Rename directory
  console.log('\n📁 Renaming domain directory...');
  renameDomainDirectory(domainName);

  console.log('\n✅ Setup complete!');
  console.log(`\nYour domain "$core" is now configured.`);
  console.log('You can start adding your schemas, workflows, tasks, and other components.\n');
}

// Run setup
setup().catch((error) => {
  console.error('❌ Setup failed:', error.message);
  process.exit(1);
});

