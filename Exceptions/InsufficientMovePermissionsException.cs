using System;

namespace SelfUpdateExecutor.Exceptions
{
    public class InsufficientMovePermissionsException : Exception
    {
        public InsufficientMovePermissionsException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}