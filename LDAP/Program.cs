// See https://aka.ms/new-console-template for more information
using LDAP.Services;


Users users = new();
Groups groups = new();
Rights rights = new();

users.GetAllUsers();

//rights.HandleRights();
//users.HandleUsers();
//groups.HandleGroups();

Console.ReadLine();