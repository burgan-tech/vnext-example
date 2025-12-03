const fs = require('fs');
const path = require('path');

// Read vnext.config.json
const vnextConfigPath = path.join(__dirname, 'vnext.config.json');
const packageJsonPath = path.join(__dirname, 'package.json');

try {
  // Read and parse vnext.config.json
  const vnextConfig = JSON.parse(fs.readFileSync(vnextConfigPath, 'utf8'));
  const schemaVersion = vnextConfig.schemaVersion;

  if (!schemaVersion) {
    console.error('Error: schemaVersion not found in vnext.config.json');
    process.exit(1);
  }

  // Read and parse package.json
  const packageJson = JSON.parse(fs.readFileSync(packageJsonPath, 'utf8'));

  // Update package.json dependencies
  if (!packageJson.dependencies) {
    packageJson.dependencies = {};
  }
  
  const schemaPackage = '@burgan-tech/vnext-schema';
  const versionRange = `^${schemaVersion}`;
  
  let packageJsonChanged = false;
  
  // Check if version needs updating
  const currentVersion = packageJson.dependencies[schemaPackage];
  if (currentVersion !== versionRange) {
    packageJson.dependencies[schemaPackage] = versionRange;
    
    // Write updated package.json
    fs.writeFileSync(
      packageJsonPath,
      JSON.stringify(packageJson, null, 2) + '\n',
      'utf8'
    );
    
    console.log(`✓ Updated ${schemaPackage} to ${versionRange} in package.json`);
    packageJsonChanged = true;
  } else {
    console.log(`✓ ${schemaPackage} is already at ${versionRange}`);
  }

  // If package.json was changed, warn that npm install may need to be run again
  if (packageJsonChanged) {
    console.log('\n⚠️  package.json was updated. Run "npm install" to install the updated dependency.');
  }

} catch (error) {
  console.error('Error syncing schema version:', error.message);
  process.exit(1);
}

