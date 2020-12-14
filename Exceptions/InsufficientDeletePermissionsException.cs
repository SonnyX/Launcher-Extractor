using System;

namespace SelfUpdateExecutor.Exceptions
{
    public class InsufficientDeletePermissionsException : Exception
    {
        public InsufficientDeletePermissionsException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}