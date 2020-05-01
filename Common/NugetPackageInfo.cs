using System;

namespace Scar.Utilities
{
    public class NugetPackageInfo
    {
        public NugetPackageInfo(string name, Version version)
        {
            Name = name ?? throw new ArgumentNullException(nameof(name));
            Version = version ?? throw new ArgumentNullException(nameof(version));
        }

        public string Name { get; }

        public Version Version { get; }

        public override string ToString()
        {
            return $"{Name}.{Version}";
        }
    }
}
