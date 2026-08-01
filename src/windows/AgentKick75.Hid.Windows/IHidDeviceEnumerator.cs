// SPDX-License-Identifier: MIT
namespace AgentKick75.Hid.Windows;

public interface IHidDeviceEnumerator
{
    IReadOnlyList<HidInterfaceDescriptor> Enumerate();
}
