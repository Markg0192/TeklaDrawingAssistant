using System;
using System.Windows;
using TeklaDrawingAssistant.Core;
using TeklaDrawingAssistant.Models;
using TeklaDrawingAssistant.Tekla;

namespace TeklaDrawingAssistant
{
    public partial class MainWindow : Window
    {
        private TeklaSession _session;
        private DrawingAutomationService _service;

        public MainWindow()
        {
            InitializeComponent();
            InitialiseTekla();
        }

        private void InitialiseTekla()
        {
            try
            {
                _session = new TeklaSession();
                _service = new DrawingAutomationService(_session);
                UpdateConnectionStatus();
            }
            catch (Exception ex)
            {
                ConnectionText.Text = "Not connected";
                LogTextBox.Text = ex.ToString();
            }
        }

        private void UpdateConnectionStatus()
        {
            if (_session == null || !_session.IsConnected)
            {
                ConnectionText.Text = "Not connected - open Tekla Structures 2023 with a model loaded.";
                return;
            }

            var drawing = _session.DrawingHandler.GetActiveDrawing();
            ConnectionText.Text = drawing == null
                ? "Connected - open a drawing to begin."
                : $"Connected - active drawing: {drawing.Mark} - {drawing.Name}";
        }

        private void RefreshButton_Click(object sender, RoutedEventArgs e)
        {
            InitialiseTekla();
        }

        private void AnalyzeButton_Click(object sender, RoutedEventArgs e)
        {
            RunSafely(() =>
            {
                LogTextBox.Text = _service.AnalyzeDimensionRulesText();
                UpdateConnectionStatus();
            });
        }

        private void DimensionButton_Click(object sender, RoutedEventArgs e)
        {
            RunSafely(() =>
            {
                var options = new DimensioningOptions
                {
                    DeleteExistingStraightDimensions = DeleteExistingCheckBox.IsChecked == true,
                    SaveDrawingAfterRun = SaveDrawingCheckBox.IsChecked == true
                };

                LogTextBox.Text = _service.DimensionHoles(options);
                UpdateConnectionStatus();
            });
        }

        private void RunSafely(Action action)
        {
            try
            {
                IsEnabled = false;
                action();
            }
            catch (Exception ex)
            {
                LogTextBox.Text = ex.ToString();
            }
            finally
            {
                IsEnabled = true;
            }
        }
    }
}
