namespace DoorBird.API.Util;

public class DeviceAccessException : Exception {
    public UserType RequiredType { get; }

    public DeviceAccessException(UserType requiredType)
        : base($"This operation requires {requiredType} access.") {
        RequiredType = requiredType;
    }
}
