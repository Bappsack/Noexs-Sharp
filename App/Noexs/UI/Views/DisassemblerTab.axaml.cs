using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Noexs.Core;
using Noexs.Util;

namespace Noexs.UI.Views;

public partial class DisassemblerTab : UserControl, IController
{
    private MainWindow mc = null!;
    private readonly ObservableCollection<CsToolWrapper.Insn> instructions = new();

    public DisassemblerTab()
    {
        InitializeComponent();
        instructionList.ItemsSource = instructions;
        instructionList.SelectionChanged += (_, _) =>
        {
            if (instructionList.SelectedItem is not CsToolWrapper.Insn i)
            {
                return;
            }
            selectedAddress.Value = i.Addr;
            selectedOp.Text = i.OpStr;
        };

        var menu = new ContextMenu();
        var create = new MenuItem { Header = new TextBlock { Text = "Create Signature" } };
        Avalonia.Automation.AutomationProperties.SetName(create, "Create Signature");
        create.Click += (_, _) =>
        {
            if (instructionList.SelectedItem is CsToolWrapper.Insn i)
            {
                mc.SetTab(MainWindow.Tab.SIGNATURES);
                mc.Signatures().CreateAt(i.Addr);
            }
        };
        menu.Items.Add(create);
        instructionList.ContextMenu = menu;
    }

    private void UpdateView()
    {
        if (!mc.RequireConnection())
        {
            return;
        }
        //TODO do this as a service
        byte[] buffer = new byte[2048];
        long addr = visibleAddress.Value;

        try
        {
            mc.GetDebugger().Readmem(addr, buffer.Length, buffer);
        }
        catch (Exception e)
        {
            Log.Error(e, "disassembler read");
            mc.SetStatus("Unable to read memory at " + HexUtils.FormatAddress(addr) + ": " + e.Message);
            return;
        }
        instructions.Clear();
        try
        {
            foreach (CsToolWrapper.Insn insn in CsToolWrapper.Disassemble(buffer, addr))
            {
                instructions.Add(insn);
            }
        }
        catch (Exception e) when (e is IOException or Win32Exception)
        {
            Log.Error(e);
            MessageDialog.ShowMessage("The disassembler is not available (built-in capstone library missing and no cstool on PATH)\n\n" + e.Message, AlertType.ERROR);
        }
    }

    public void SetDisassembleAddress(long address)
    {
        visibleAddress.Value = address;
        UpdateView();
    }

    public void SetMainController(MainWindow c)
    {
        mc = c;
    }

    public void OnConnect()
    {
        visiblePane.IsEnabled = true;
        selectedPane.IsEnabled = true;
        callstackPane.IsEnabled = true;
    }

    public void OnDisconnect()
    {
        visiblePane.IsEnabled = false;
        selectedPane.IsEnabled = false;
        callstackPane.IsEnabled = false;
    }

    private void Update(object? sender, RoutedEventArgs e)
    {
        UpdateView();
    }

    private void AssembleOp(object? sender, RoutedEventArgs e)
    {
        if (!mc.RequireConnection())
        {
            return;
        }
        mc.Guard(AssembleOp);
    }

    private void AssembleOp()
    {
        long addr = selectedAddress.Value;
        string op = selectedOp.Text ?? "";
        try
        {
            byte[]? b;
            try
            {
                b = CsToolWrapper.AssembleInstruction(op, addr);
            }
            catch (Arm64Assembler.AsmException ex)
            {
                MessageDialog.ShowMessage("Unable to assemble \"" + op + "\":\n" + ex.Message, AlertType.WARNING);
                return;
            }
            if (b == null)
            {
                return;
            }
            mc.SetStatus("Assembled \"" + op + "\" -> " + Convert.ToHexString(b) + " at " + HexUtils.FormatAddress(addr));
            Result r = mc.GetDebugger().Writemem(b, addr);
            Log.Info("assemble: " + r);
            if (r.Succeeded)
            {
                UpdateView();
            }
            else
            {
                MessageDialog.ShowMessage("Unable to assemble \"" + op + "\"", AlertType.ERROR);
            }
        }
        catch (Exception ex) when (ex is IOException or Win32Exception)
        {
            Log.Error(ex);
            MessageDialog.ShowMessage("Unable to run kstool (is keystone installed and on PATH?)\n\n" + ex.Message, AlertType.ERROR);
        }
    }
}
