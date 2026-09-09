namespace LoGuard.Sdk;

public class LoGuardException(string message) : Exception(message);
public class LoGuardAuthException(string message) : LoGuardException(message);
public class LoGuardConnectionException(string message) : LoGuardException(message);
public class LoGuardValidationException(string message) : LoGuardException(message);
public class LoGuardNotFoundException(string message) : LoGuardException(message);
public class LoGuardConflictException(string message) : LoGuardException(message);
public class LoGuardQuotaException(string message) : LoGuardException(message);
public class LoGuardNotInitializedException(string message) : LoGuardException(message);
