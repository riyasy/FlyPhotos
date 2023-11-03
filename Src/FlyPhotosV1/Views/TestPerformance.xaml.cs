using FlyPhotos.Data;
using FlyPhotos.Utils;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;

namespace FlyPhotos.Views;

/// <summary>
/// Interaction logic for TestPerformance.xaml
/// </summary>
public partial class TestPerformance
{
    private const string DirPath = @"M:\Photos\Photos\Digicam\A6400\2023\";
    private readonly ConcurrentBag<Photo> _cache = new();
    private List<string> _files = new();

    public TestPerformance()
    {
        InitializeComponent();
    }

    private void btnUIThreadTest_Click(object sender, RoutedEventArgs e)
    {
        var ext = new List<string> { "jpg", "heic", "arw", "nef" };
        _files = Directory.EnumerateFiles(DirPath, "*.*", SearchOption.TopDirectoryOnly)
            .Where(s => ext.Contains(Path.GetExtension(s).TrimStart('.').ToLowerInvariant())).ToList();

        SliderPhotoSelect.Minimum = 0;
        SliderPhotoSelect.Maximum = _files.Count - 1;

        Task.Run(RunInParallelFor);
    }

    private void RunInParallelFor()
    {
        var watch = Stopwatch.StartNew();

        for (var index = 0; index < Math.Min(_files.Count, 20); index++)
        {
            var file = _files[index];

            var photo = ImageUtil.GetPreview(file);
            _cache.Add(photo);

            //var res = Task.Run(() => GetInitialPreview(file));
            //res.Wait();
            //continue;

            //void GetInitialPreview(string fileName)
            //{
            //    var photo = ImageUtil.GetPreview(fileName);
            //    _cache.Add(photo);
            //}
        }

        //for (var j = 0; j < 5; j++)
        //    Parallel.ForEach(_myFiles, file =>
        //    {
        //        WpfWicReader.TryGetHqImageThruExternalDecoder(file, out var photo);
        //        _cache.Add(photo.Bitmap);
        //    });

        //for (var j = 0; j < 10; j++)
        //{
        //    foreach (var file in _myFiles)
        //    {
        //        WpfWicReader.TryGetHqImageThruExternalDecoder(file, out var photo);
        //    }
        //}
        watch.Stop();
        var elapsedMs = watch.ElapsedMilliseconds;
        MessageBox.Show("Time taken :- " + elapsedMs + " No of Items :- " + _cache.Count);
    }

    private void sliderPhotoSelect_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        var index = Convert.ToInt32(SliderPhotoSelect.Value);
        PhotoDisplayy.Source = _cache.ElementAt(index).Bitmap;
    }

    private void Button_Click(object sender, RoutedEventArgs e)
    {
        if (_cache.Count > 0 && _files.Count == 1) PhotoDisplayy.Source = _cache.First().Bitmap;
    }
}

// TEST 1 - on UI Thread
//Task.Run(() =>
//{
//    Parallel.For(0, myFiles.Count,
//        new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount },
//        (index, loopState) =>
//        {
//            var image = ImageUtil.LoadImageFile(myFiles[index]);
//            if (_stopProcessing) loopState.Stop();
//        });
//    watch.Stop();
//    var elapsedMs = watch.ElapsedMilliseconds;
//    MessageBox.Show("Time taken :- " + elapsedMs + " No of Items :- " + _cache.Count());
//});

//Parallel.ForEach<string>(myFiles,
//    new ParallelOptions() { MaxDegreeOfParallelism = Environment.ProcessorCount },
//    (val, loopState) =>
//    {
//        var image = ImageUtil.LoadImageFile(val);
//        if (stopProcessing) 
//        {
//            loopState.Stop();
//        }
//    });

//List<Task> taskList   = new List<Task>();
//for (var index = 0; index < myFiles.Count; index++)
//{
//    var i = index;
//    var task = Task.Run(() =>
//    {
//        var image = ImageUtil.LoadImageFile(myFiles[i]);
//    });
//    taskList.Add(task);
//}
//Task.WaitAll(taskList.ToArray());

//for (var index = 0; index < myFiles.Count; index++)
//{
//    var image = ImageUtil.LoadImageFile(myFiles[index]);
//}

// TEST 2 Parallel foreach - internally spawns threads.

//Parallel.ForEach(myFiles, file =>
//{
//    cache.Add(GetImageThruBMI(file));
//});

// TEST 3 Separate task for each file - internally spawns threads.

//List<Task> TaskList = new List<Task>();
//foreach (var file in myFiles)
//{
//    Task task = new Task(() =>
//    {
//        cache.Add(GetImageThruBMI(file));
//    });
//    task.Start();
//    TaskList.Add(task);
//}
//Task.WaitAll(TaskList.ToArray());

// Test 3 - Three separate tasks

//List<Task> TaskList = new List<Task>();
//const int taskCount = 4;

//for (int i = 0; i < taskCount; i++)
//{
//    int iterVal = i;
//    Task task1 = new Task(() =>
//    {
//        int totalCount = myFiles.Count();
//        int from = (totalCount * iterVal) / taskCount;
//        int to = (totalCount * (iterVal + 1)) / taskCount;
//        for (int j = from; j < to; j++)
//        {
//            string? file = myFiles[j];
//            //LaunchExtProgram(file);
//            var bs = ImageUtil.LoadImageFile(file);
//            cache.Add(bs);
//        }
//    });
//    TaskList.Add(task1);
//    task1.Start();

//}
//Task.WaitAll(TaskList.ToArray());