const fs = require('fs');
const path = require('path');

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

// Find the domain directory from config
function findDomainDirectory() {
  const pathsConfig = getPathsConfig();
  return pathsConfig.componentsRoot;
}

// Load JSON files from a directory
function loadJsonFiles(dirPath) {
  const files = {};
  if (!fs.existsSync(dirPath)) {
    return files;
  }
  
  const entries = fs.readdirSync(dirPath, { withFileTypes: true });
  for (const entry of entries) {
    // Skip .meta directory
    if (entry.name === '.meta') {
      continue;
    }
    
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
    return getPathsConfig();
  },
  
  // Get paths configuration
  getPathsConfig: function() {
    return getPathsConfig();
  },
  
  // Get all schemas
  getSchemas: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    const pathsConfig = getPathsConfig();
    return loadJsonFiles(path.join(domainDir, pathsConfig.schemas));
  },
  
  // Get all workflows
  getWorkflows: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    const pathsConfig = getPathsConfig();
    return loadJsonFiles(path.join(domainDir, pathsConfig.workflows));
  },
  
  // Get all tasks
  getTasks: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    const pathsConfig = getPathsConfig();
    return loadJsonFiles(path.join(domainDir, pathsConfig.tasks));
  },
  
  // Get all views
  getViews: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    const pathsConfig = getPathsConfig();
    return loadJsonFiles(path.join(domainDir, pathsConfig.views));
  },
  
  // Get all functions
  getFunctions: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    const pathsConfig = getPathsConfig();
    return loadJsonFiles(path.join(domainDir, pathsConfig.functions));
  },
  
  // Get all extensions
  getExtensions: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    const pathsConfig = getPathsConfig();
    return loadJsonFiles(path.join(domainDir, pathsConfig.extensions));
  },
  
  // Get available component types
  getAvailableTypes: function() {
    const pathsConfig = getPathsConfig();
    return [pathsConfig.schemas, pathsConfig.workflows, pathsConfig.tasks, pathsConfig.views, pathsConfig.functions, pathsConfig.extensions];
  },
  
  // Get domain directory name
  getDomainName: function() {
    return findDomainDirectory();
  },
  
  // Get component path for a specific type
  getComponentPath: function(componentType) {
    const domainDir = findDomainDirectory();
    if (!domainDir) return null;
    const pathsConfig = getPathsConfig();
    const pathKey = componentType.toLowerCase();
    if (pathsConfig[pathKey]) {
      return path.join(domainDir, pathsConfig[pathKey]);
    }
    return null;
  }
};
