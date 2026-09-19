namespace LoGuard.Sdk;

/// <summary>Base type for all exceptions thrown by the LoGuard SDK.</summary>
public class LoGuardException(string message) : Exception(message);

/// <summary>Thrown when the API key is invalid, missing, or the subscription has expired.</summary>
public class LoGuardAuthException(string message) : LoGuardException(message);

/// <summary>Thrown when the LoGuard API is unreachable, times out, or returns a server error after retries are exhausted.</summary>
public class LoGuardConnectionException(string message) : LoGuardException(message);

/// <summary>Thrown when event data or client configuration fails validation before being sent.</summary>
public class LoGuardValidationException(string message) : LoGuardException(message);

/// <summary>Thrown when a requested resource (e.g. an alert rule) does not exist.</summary>
public class LoGuardNotFoundException(string message) : LoGuardException(message);

/// <summary>Thrown when creating a resource that already exists (e.g. a duplicate alert rule).</summary>
public class LoGuardConflictException(string message) : LoGuardException(message);

/// <summary>Thrown when the account's monthly event quota has been exceeded.</summary>
public class LoGuardQuotaException(string message) : LoGuardException(message);

/// <summary>Thrown when an operation is attempted on a client that hasn't been configured yet.</summary>
public class LoGuardNotInitializedException(string message) : LoGuardException(message);
