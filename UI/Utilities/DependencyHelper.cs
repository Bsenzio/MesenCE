using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;

namespace Mesen.Utilities;

class DependencyHelper
{
	public static void ExtractNativeDependencies(string dest)
	{
		using(Stream? depStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mesen.Dependencies.zip")) {
			if(depStream == null) {
				throw new Exception("Missing dependencies.zip");
			}

			try {
				//In builds done without the "OptimizeUi" flag (Debug, or a plain Release build), the core is not embedded
				//in the .exe (to improve build performance): copy it from the folder that contains Mesen.exe to the home folder.
				//(HD Studio fork: this used to be Debug-only and used the current directory, so Release builds kept
				//loading an outdated core from the home folder.)
				string[] extensions = new string[] { ".dll", ".so", ".dylib" };
				string exeFolder = AppContext.BaseDirectory;
				foreach(string ext in extensions) {
					string src = Path.Join(exeFolder, "MesenCore" + ext);
					string target = Path.Join(dest, "MesenCore" + ext);
					if(File.Exists(src) && !string.Equals(Path.GetFullPath(src), Path.GetFullPath(target), StringComparison.OrdinalIgnoreCase)) {
						FileInfo srcInfo = new(src);
						FileInfo targetInfo = new(target);
						if(!targetInfo.Exists || targetInfo.LastWriteTimeUtc != srcInfo.LastWriteTimeUtc || targetInfo.Length != srcInfo.Length) {
							File.Copy(src, target, true);
						}
					}
				}
			} catch { }

			using ZipArchive zip = new(depStream);
			foreach(ZipArchiveEntry entry in zip.Entries) {
				try {
					if(entry.FullName.StartsWith("Internal")) {
						continue;
					}

					string path = Path.Combine(dest, entry.FullName);
					entry.ExternalAttributes = 0;
					if(File.Exists(path)) {
						if(Path.GetExtension(path)?.ToLower() == ".bin") {
							//Don't overwrite BS-X bin files if they already exist on the disk
							continue;
						}

						FileInfo fileInfo = new(path);
						if(fileInfo.LastWriteTime != entry.LastWriteTime || fileInfo.Length != entry.Length) {
							entry.ExtractToFile(path, true);
						}
					} else {
						string? folderName = Path.GetDirectoryName(path);
						if(folderName != null && !Directory.Exists(folderName)) {
							//Create any missing directory (e.g Satellaview)
							Directory.CreateDirectory(folderName);
						}
						entry.ExtractToFile(path, true);
					}
				} catch {

				}
			}
		}
	}

	public static string? GetFileContent(string filename)
	{
		using Stream? depStream = Assembly.GetExecutingAssembly().GetManifestResourceStream("Mesen.Dependencies.zip");
		if(depStream != null) {
			using ZipArchive zip = new(depStream);
			foreach(ZipArchiveEntry entry in zip.Entries) {
				if(entry.Name == filename) {
					using Stream entryStream = entry.Open();
					using StreamReader reader = new StreamReader(entryStream);
					return reader.ReadToEnd();
				}
			}
		}
		return null;
	}
}
