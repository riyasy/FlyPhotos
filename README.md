
# Fly Photos

Fly Photos is one of the fastest photoviewer for Windows. It tries to mimic many features of the now extinct Google Picasa Photo Viewer. This project was born out of finding the ideal replacement for Google Picasa Photo Viewer.

![](https://github.com/riyasy/FlyPhotos/blob/main/Misc/ImagesForDocumentation/03FullScreen.png?raw=true)

Watch Fly Photos in action

[![Fly Photos](https://markdown-videos-api.jorgenkh.no/url?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DQkL2-WYY2Ic%26t%3D12s)](https://www.youtube.com/watch?v=QkL2-WYY2Ic&t=12s)

| Version        	| 1.x                      	| 2.x             	|
|----------------	|--------------------------	|-----------------	|
| UI Technology     	| WPF                      	| WinUI, Win2D    	|
| OS             	| Windows 7,8,10,11        	| Windows 10,11   	|
| Release        	| Stable                   	| RC              	|
| Caching        	| Slightly Faster than 2.x 	|                 	|
| Pan/Zoom       	|                          	| Faster than 1.x 	|
| Zoom Animation 	| No                       	| Yes             	|

### Features
1. **Full Screen mode with transparent background like in Picasa Photo Viewer.**
2. Fast startup even for raw files with image size greater than 100 MB.
3. Full Screen exit on pressing anywhere outside the image.
4. Close on pressing escape key.
5. Use < or > keys on screen or on keyboard to navigate between photos.
6. **Long press < or > to fly through all cached images on left and right.** This makes using thumbnails redundant as traversal between photos is so fast.
7. Fully in memory caching (nothing is created on disk)
8. No files are opened in write mode. So your images are fully safe.
9. **Respects Windows Explorer sort order and filtering.** For e.g, if we right click and open an image in the Recent Items view of explorer, the App shows only items in the recent items view. Likewise, if we have filtered images using explorer search-filter and open one image, navigation moves through only the filtered list.
10. Rounded image borders.
11. Cache size - Tries to cache 300 low quality previews in both direction. Tries to cache 2 HQ previews in both direction. This figure can be made editable in future versions.

### Limitations
1. Has a max path length limit of 256 characters. If image is present in a path with more than 256 characters, the nearby images will not be listed. Will be fixed in later revisions.
2. Supports image formats supported by Windows Imaging Component only. For Windows 11, the format list is exhaustive. (Same as formats which can be opened by Windows Photos App). For Windows 7, this list of formats is small. But if any WIC compliant codec is already installed in the PC, then Fly can automatically detect and load the photo. Check *Settings>Show Codecs* button to know the detected OS supported formats.


### Installation
Currently only 64 bit versions of Windows 7, 8, 10 and 11 are supported. There are two installers. Choose based on your preference. 

1. *FlyPhotosInstaller_ver_SelfContained.msi*  - .Net runtime is also inlcuded with installer and is installed in the application folder.  App has no other dependencies and can be directly run after installation.(size around 76 MB)
2. *FlyPhotosInstaller_ver_RuntimeDependent.msi*  - Bare installer without .Net Runtime. If .Net Runtime is already installed on your machine OR if you wish to use the latest installer from Microsoft, please use the bare installer which is small in size (<10 MB). In this case, if the App is started in a PC without .Net runtime, an error box comes like this. The .Net runtime can be installed from the error box itself.

![](https://github.com/riyasy/FlyPhotos/blob/main/Misc/ImagesForDocumentation/01netRuntimeError.png?raw=true)

### How to use
Right click any image and click the **Open with Fly** menu item to see the App. In Windows 7,8 and 10 this menu will be accessible on right click itself. With Windows 11, sometimes we may need to press **Show more options** to see the **Open with Fly** menu. With regular usage in Windows 11, the **Open with Fly** menu will get promoted to the main menu.

![](https://github.com/riyasy/FlyPhotos/blob/main/Misc/ImagesForDocumentation/02RightClickMenu.png?raw=true)

Click the < or > buttons or keyboard to navigate. After cache is created long press these buttons to fly through the list of images.





