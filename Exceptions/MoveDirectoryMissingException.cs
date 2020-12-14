using System;

namespace SelfUpdateExecutor.Exceptions
{
    public class MoveDirectoryMissingException : Exception
    {
        public MoveDirectoryMissingException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}