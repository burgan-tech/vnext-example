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
    extensions: 'Extensions',
    mappings: 'Mappings'
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

// Load JSON files from a directory (recursively; basename is the map key)
function loadJsonFiles(dirPath) {
  const files = {};
  if (!fs.existsSync(dirPath)) {
    return files;
  }

  const walk = (currentDir) => {
    const entries = fs.readdirSync(currentDir, { withFileTypes: true });
    for (const entry of entries) {
      if (entry.name === '.meta') {
        continue;
      }
      const fullPath = path.join(currentDir, entry.name);
      if (entry.isDirectory()) {
        walk(fullPath);
      } else if (entry.isFile() && entry.name.endsWith('.json')) {
        const baseName = entry.name.replace(/\.json$/i, '');
        if (Object.prototype.hasOwnProperty.call(files, baseName)) {
          console.warn(
            `Warning: Duplicate JSON basename "${baseName}" under ${dirPath}; keeping first, skipping ${fullPath}`
          );
          continue;
        }
        try {
          files[baseName] = JSON.parse(fs.readFileSync(fullPath, 'utf8'));
        } catch (error) {
          console.warn(`Warning: Could not load ${fullPath}: ${error.message}`);
        }
      }
    }
  };

  walk(dirPath);
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

  // Get all mappings
  getMappings: function() {
    const domainDir = findDomainDirectory();
    if (!domainDir) return {};
    const pathsConfig = getPathsConfig();
    return loadJsonFiles(path.join(domainDir, pathsConfig.mappings));
  },
  
  // Get available component types
  getAvailableTypes: function() {
    const pathsConfig = getPathsConfig();
    return [pathsConfig.schemas, pathsConfig.workflows, pathsConfig.tasks, pathsConfig.views, pathsConfig.functions, pathsConfig.extensions, pathsConfig.mappings];
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
