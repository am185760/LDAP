namespace LDAP.Models;

public class LdapSettings
{
    public string LdapUrl { get; set; } = string.Empty;
    public string DomainName { get; set; } = string.Empty;
    public string BaseDN { get; set; } = string.Empty;
    public string UsersOU { get; set; } = string.Empty;
    public string GroupsOU { get; set; } = string.Empty;
    public string ServiceAccountUsername { get; set; } = string.Empty;
    public string ServiceAccountPassword { get; set; } = string.Empty;
}
