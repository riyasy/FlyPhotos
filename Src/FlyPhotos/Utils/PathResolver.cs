using System;
using System.IO;
using Windows.Storage;


namespace FlyPhotos.Utils
{
    internal static class PathResolver
    {
        public static string GetDbFolderPath()
        {
            var dbFolderPath = App.Packaged ?
                ApplicationData.Current.LocalFolder.Path :
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FlyPhotos");

            if (!App.Packaged && !Directory.Exists(dbFolderPath))
                Directory.CreateDirectory(dbFolderPath);

            return dbFolderPath;
        }

        public static string GetLogFolderPath()
        {
            var logFolder = App.Packaged ?
                ApplicationData.Current.LocalFolder.Path :
                Path.Combine(Path.GetTempPath(), "FlyPhotos");

            if (!App.Packaged && !Directory.Exists(logFolder))
                Directory.CreateDirectory(logFolder);

            return logFolder;
        }

        public static string GetDefaultSettingsFolder()
        {
            return AppContext.BaseDirectory;
        }

        public static string GetUserSettingsFolder()
        {
            var userSettingsFolder = App.Packaged
                ? ApplicationData.Current.LocalFolder.Path
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "FlyPhotos");

            if (!App.Packaged && !Directory.Exists(userSettingsFolder)) 
                Directory.CreateDirectory(userSettingsFolder);

            return userSettingsFolder;
        }

        public static string GetExternalWicReaderExePath()
        {
            var exePath = App.Packaged 
                ? Path.Combine(ApplicationData.Current.LocalFolder.Path, "WicImageFileReaderNative.exe")
                : Path.Combine(AppContext.BaseDirectory, "WicImageFileReaderNative.exe");
            return exePath;
        }

        public static IStorageFolder GetExternalWicReaderExeCopyFolderForPackagedApp()
        {
            return ApplicationData.Current.LocalFolder;
        }
    }
}
