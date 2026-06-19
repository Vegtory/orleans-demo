namespace App.Api.GrainContracts;

/// <summary>
/// A simple per-id counter. The grain key is the counter id (e.g. "demo").
/// </summary>
public interface ICounterGrain : IGrainWithStringKey
{
    Task<int> Get();
    Task<int> Increment();
    Task Reset();
}
