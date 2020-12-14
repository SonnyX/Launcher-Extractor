#nullable enable

using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using SelfUpdateExecutor.Exceptions;
using Process = System.Diagnostics.Process;

namespace SelfUpdateExecutor
{
    public class SelfUpdateExecutor
    {
        /** Constants */
        private const int ExitProcessTimeoutInMs = 10_000; // 10 seconds
        private const string LauncherExeFilename = "Renegade X Launcher.exe";
        private readonly string _applicationLogFilePath;
        private const string TargetPathSwitch = "--target=";
        private const string ProcessIdSwitch = "--pid=";

        private SelfUpdateExecutor()
        {
            _applicationLogFilePath = Path.GetTempFileName();
        }

        /// <summary>
        /// Executes a launcher update by attempting to apply a launcher update, then restarting the launcher
        /// </summary>
        /// <param name="args"></param>
        public static void Main(string[] args)
        {
            string? targetPath = null;
            int processId = 0;
            List<string> errors = new List<string>();

            foreach (string arg in args)
            {
                if (arg.StartsWith(TargetPathSwitch))
                {
                    // Process targetPath
                    targetPath = arg.Substring(TargetPathSwitch.Length);
                    if (targetPath.EndsWith("\\") || targetPath.EndsWith("/"))
                    {
                        targetPath = targetPath.Substring(0, targetPath.Length - 1);
                    }
                }
                else if (arg.StartsWith(ProcessIdSwitch))
                {
                    // Process processId
                    processId = int.Parse(arg.Substring(ProcessIdSwitch.Length));
                    if (processId == 0)
                        errors.Add("pid should not be empty or 0!");
                }
                else
                {
                    errors.Add($"Unknown argument: {arg}");
                }
            }

            if (errors.Count != 0 || targetPath == null || processId == 0)
            {
                Console.Error.WriteLine(string.Join(Environment.NewLine, errors));
                Console.Out.WriteLine("Usage: SelfUpdateExecutor.exe --target=<fully_qualified_path> --pid=<processId>");
                return;
            }

            SelfUpdateExecutor selfUpdateExecutor = new SelfUpdateExecutor();
            selfUpdateExecutor.TryExecuteLauncherUpdate(targetPath, processId);
        }

        private void TryExecuteLauncherUpdate(string absoluteTarget, int processId)
        {
            // Setup default values
            string? absoluteSourcePath = Path.GetDirectoryName(AppContext.BaseDirectory);
            SelfUpdateStatus status = SelfUpdateStatus.Success;

            // Execute update
            if (!string.IsNullOrEmpty(absoluteSourcePath) && !string.IsNullOrEmpty(absoluteTarget) && processId != 0 /*&& Path.IsPathFullyQualified(absoluteTarget)*/)
            {
                try
                {
                    UpdateLauncher(absoluteSourcePath, absoluteTarget, processId);
                }
                catch (Exception e)
                {
                    Error(e.Message);
                    status = e switch
                    {
                        { } ex when ex is InsufficientDeletePermissionsException => SelfUpdateStatus.DeletePermissionFailure,
                        { } ex when ex is InsufficientMovePermissionsException => SelfUpdateStatus.MovePermissionFailure,
                        { } ex when ex is MoveDirectoryMissingException => SelfUpdateStatus.DirectoryMissingFailure,
                        { } ex when ex is CannotKillProcessException => SelfUpdateStatus.KillFailure,
                        _ => SelfUpdateStatus.UnhandledException
                    };
                }
            }
            else
            {
                if (string.IsNullOrEmpty(absoluteSourcePath) || processId != 0)
                {
                    Error("pid, or sourcePath was null or empty.");
                    status = SelfUpdateStatus.InvalidArguments;
                }
                else
                {
                    string errorMessage = $"The argument supplied by --target was null, empty or not absolute: {absoluteTarget}";
                    throw new ArgumentException(errorMessage);
                }
            }

            // Startup new launcher; failure is irresolvable
            Directory.SetCurrentDirectory(absoluteTarget);
            Process.Start($"{absoluteTarget}/{LauncherExeFilename}", $"--patch-result={status} --application-log={_applicationLogFilePath}");
        }

        /// <summary>
        ///   Represents update success/failure
        /// </summary>
        private enum SelfUpdateStatus
        {
            Success = 0,
            InvalidArguments = 1,
            KillFailure = 2,
            DeletePermissionFailure = 3,
            MovePermissionFailure = 4,
            DirectoryMissingFailure = 5,
            UnhandledException = 6,
            UnknownError = 7
        }

        /// <summary>
        ///   Logs a log message to stdout
        /// </summary>
        /// <param name="message">Log message to write to stdout</param>
        private void Log(string message)
        {
            File.AppendAllLines(_applicationLogFilePath, new[] { $"[INFO] {message}" });
            Console.Out.WriteLine(message);
        }

        /// <summary>
        ///   Logs a log message to stderr
        /// </summary>
        /// <param name="message">Log message to write to stderr</param>
        private void Error(string message)
        {
            File.AppendAllLines(_applicationLogFilePath, new[] { $"[ERROR] {message}" });
            Console.Error.WriteLine(message);
        }

        /// <summary>
        ///   Executes a launcher update by attempting to apply a launcher update, then restarting the launcher
        /// </summary>
        /// <param name="absolutePathOfNewLauncher">Path of the new launcher binaries</param>
        /// <param name="absoluteTarget">Path of the current launcher binaries</param>
        /// <param name="processId">Process ID of the launcher which blocks us from updating</param>
        private void UpdateLauncher(string absolutePathOfNewLauncher, string absoluteTarget, int processId)
        {
            // Set working directory to the user's temporary folder so that we're not locking any directories.
            Directory.SetCurrentDirectory(Path.GetTempPath());

            string absoluteBackupPath = $"{absolutePathOfNewLauncher}_backup";
            // Apply launcher self-update

            WaitForProcess(processId);
            // Delete backup directory if it still exists from an previous upgrade.
            DeleteDirectory(absoluteBackupPath);
            MoveDirectory(absolutePathOfNewLauncher, absoluteBackupPath);
            try
            {
                MoveDirectory(absoluteTarget, absolutePathOfNewLauncher);
                DeleteDirectory(absoluteBackupPath);
            }
            catch (UnauthorizedAccessException)
            {
                // We don't have permission to move the `updatePath` directory, instead move `backupPath` back
                try
                {
                    MoveDirectory(absoluteBackupPath, absolutePathOfNewLauncher);
                }
                catch
                {
                    throw new Exception("Backup failed to restore; this should never happen");
                }
            }
        }

        /// <summary>
        /// Waits 10 seconds for a process to close gracefully, and then kills the process afterwards if it never closed.
        /// </summary>
        /// <param name="processId">Process ID of the task to wait on</param>
        private void WaitForProcess(int processId)
        {
            // Wait for launcher to close (failure is fatal)
            try
            {
                Log("Waiting for launcher to close...");
                Process process = Process.GetProcessById(processId);
                if (!process.WaitForExit(ExitProcessTimeoutInMs))
                {
                    Log("Launcher hasn't closed gracefully; killing launcher process...");
                    // Process failed to exit gracefully; murder it
                    process.Kill();
                }
            }
            catch (ArgumentException) { } // Process doesn't exist; already closed
            catch (InvalidOperationException) { } // Process doesn't exist; already closed
            catch (Win32Exception e) // Process couldn't be killed; update failed
            {
                throw new CannotKillProcessException($"Unable to kill launcher process", e);
            }
            // Process has ended successfully
            Log("Launcher closed; applying update...");
        }

        /// <summary>
        /// Deletes a directory, and all of the files in it
        /// </summary>
        /// <param name="directory">Directory to delete</param>
        private static void DeleteDirectory(string directory)
        {
            try
            {
                // Delete the directory
                Directory.Delete(directory, true);
            }
            catch (DirectoryNotFoundException) { } // Directory does not exist
            catch (Exception) // Some other error
            {
                try
                {
                    File.Delete(directory); // Try deleting it as a file
                }
                catch (Exception e)
                {
                    throw new InsufficientDeletePermissionsException($"Failed to delete file/directory \"{directory}\"", e);
                }
            }
        }

        /// <summary>
        ///   Moves a directory from one place to another
        /// </summary>
        /// <param name="absoluteFrom">Directory to move</param>
        /// <param name="absoluteTo">Target to move directory to</param>
        private void MoveDirectory(string absoluteFrom, string absoluteTo)
        {
            try
            {
                try
                {
                    // Move the directory
                    Directory.Move(absoluteFrom, absoluteTo);
                }
                catch (IOException)
                {
                    Log($"Could not move directory \"{absoluteFrom}\" to \"{absoluteTo}\", attempting CopyDirectory instead.");
                    // We're likely attempting to move across volumes; attempt a copy instead
                    CopyDirectory(absoluteFrom, absoluteTo);
                }
            }
            catch (UnauthorizedAccessException e)
            {
                throw new InsufficientMovePermissionsException($"Failed to move file/directory from \"{absoluteFrom}\" to \"{absoluteTo}\"", e);
            }
            catch (DirectoryNotFoundException e) // We're trying to move something that doesn't exist
            {
                throw new MoveDirectoryMissingException($"Failed to move file/directory from \"{absoluteFrom}\" to \"{absoluteTo}\"", e);
            }
        }

        /// <summary>
        ///   Moves a directory from one place to another
        /// </summary>
        /// <param name="absoluteFrom">Directory to move</param>
        /// <param name="absoluteTo">Target to move directory to</param>
        private static void CopyDirectory(string absoluteFrom, string absoluteTo)
        {
            // Create target directory
            Directory.CreateDirectory(absoluteTo);

            // Copy files from source to target
            List<string> files = Directory.GetFiles(absoluteFrom).ToList();
            files.ForEach(file =>
            {
                File.Copy(file, $"{absoluteTo}/{Path.GetFileName(file)}", true);
                File.Delete(file);
            });

            // Copy subdirectories from source to target
            List<string> directories = Directory.GetDirectories(absoluteFrom).ToList();
            directories.ForEach(directory =>
            {
                CopyDirectory(directory, $"{absoluteTo}/{Path.GetFileName(directory)}");
                DeleteDirectory(directory);
            });
        }
    }
}