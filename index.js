const fs = require('fs');
const path = require('path');

// Find the domain directory dynamically
function findDomainDirectory() {
  const entries = fs.readdirSync('.', { withFileTypes: true });
  for (const entry of entries) {
    if (entry.isDirectory() && 
        !entry.name.startsWith('.') && 
        entry.name !== 'node_modules' &&
        entry.name !== 'dist') {
      // Check if it contains typical vnext structure
      const domainPath = entry.name;
      if (fs.existsSync(path.join(domainPath, 'Schemas')) ||
          fs.existsSync(path.join(domainPath, 'Workflows')) ||
          fs.existsSync(path.join(domainPath, 'Tasks'))) {
        return domainPath;
      }
    }
  }
  return null;
}

// Load JSON files from a directory
function loadJsonFiles(dirPath) {
  const files = {};
  if (!fs.existsSync(dirPath)) {
    return files;
  }
  
  const entries = fs.readdirSync(dirPath, { withFileTypes: true });
  for (const entry of entries) {
    if (entry.isFile() && entry.name.endsWith('.json')) {
      try {
        const filePath = path.join(dirPath, entry.name);
        const content = JSON.parse(fs.readFileSync(filePath, 'utf8'));
        const baseName = entry.name.replace('.json', '');
        files[baseName] = content;
      } catch (error) {
        console.warn(`Warning: Could not load ${entry.name}: ${error.message}`);
      }
    }
  }
  return files;
}

// Main module exports
module.exports = {
  // Get the domain configuration
  getDomainConfig: function() {
    try {
      return JSON.parse(fs.readFileSync('vnext.config.json', 'utf8'));
    } catch (error) {
      return null;
    }
  },
  
  // Get all schemas
  getSchemas: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    return loadJsonFiles(path.join(domainDir, 'Schemas'));
  },
  
  // Get all workflows
  getWorkflows: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    return loadJsonFiles(path.join(domainDir, 'Workflows'));
  },
  
  // Get all tasks
  getTasks: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    return loadJsonFiles(path.join(domainDir, 'Tasks'));
  },
  
  // Get all views
  getViews: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    return loadJsonFiles(path.join(domainDir, 'Views'));
  },
  
  // Get all functions
  getFunctions: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    return loadJsonFiles(path.join(domainDir, 'Functions'));
  },
  
  // Get all extensions
  getExtensions: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    return loadJsonFiles(path.join(domainDir, 'Extensions'));
  },
  
  // Get available component types
  getAvailableTypes: function() {
    return ['schemas', 'workflows', 'tasks', 'views', 'functions', 'extensions'];
  },
  
  // Get domain directory name
  getDomainName: function() {
    return findDomainDirectory();
  }
};
