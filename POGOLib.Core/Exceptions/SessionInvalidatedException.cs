using System;

namespace POGOLib.Official.Exceptions
{
    public class SessionInvalidatedException : Exception
    {
        public SessionInvalidatedException()
        {
        }

        public SessionInvalidatedException(string message) : base(message)
        {
        }

        public SessionInvalidatedException(string message, Exception innerException) : base(message, innerException)
        {
        }
    }
}
