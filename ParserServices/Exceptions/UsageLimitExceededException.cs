using System;

namespace ParserServices.Exceptions
{
    public class UsageLimitExceededException : Exception
    {
        public UsageLimitExceededException(int limit)
            : base($"Monthly usage limit of {limit} has been exceeded.")
        {
        }
    }
}
