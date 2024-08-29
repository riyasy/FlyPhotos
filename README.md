
# Fly Photos

Fly Photos is one of the fastest photoviewer for Windows. It tries to mimic many features of the now extinct Google Picasa Photo Viewer. This project was born out of finding the ideal replacement for Google Picasa Photo Viewer.

![](https://github.com/riyasy/FlyPhotos/blob/main/Misc/ImagesForDocumentation/03FullScreen.png?raw=true)

Watch Fly Photos in action

[![Fly Photos](https://markdown-videos-api.jorgenkh.no/url?url=https%3A%2F%2Fwww.youtube.com%2Fwatch%3Fv%3DQkL2-WYY2Ic%26t)](https://www.youtube.com/watch?v=QkL2-WYY2Ic&t)


### Features
1. **Full Screen mode with transparent background like in Picasa Photo Viewer.**
2. Fast startup even for raw files with image size greater than 100 MB.
3. Full Screen exit on pressing anywhere outside the image.
4. Close on pressing escape key.
5. Use < or > keys on screen or on keyboard to navigate between photos.
6. **Long press < or > to fly through all cached images on left and right.** This makes using thumbnails redundant as traversal between photos is so fast.
7. Fully in memory caching enhanced now with disk caching for previews.
8. No files are opened in write mode. So your images are fully safe.
9. **Respects Windows Explorer sort order and filtering.** For e.g, if we right click and open an image in the Recent Items view of explorer, the App shows only items in the recent items view. Likewise, if we have filtered images using explorer search-filter and open one image, navigation moves through only the filtered list.
10. Thumbnail strip as in Picasa photo viewer. (Experimental, more coding needed to make it interactive)

### Limitations
1. Supports image formats supported by Windows Imaging Component only. For Windows 11, the format list is exhaustive. (Same as formats which can be opened by Windows Photos App). For Windows 10, this list of formats is less than Windows 11. But if any WIC compliant codec is already installed in the PC, then Fly can automatically detect and load the photo. Check *Settings>Show Codecs* button to know the detected OS supported formats.


### Installation
Currently only 64 bit versions of Windows 7, 8, 10 and 11 are supported. Download and install Ver 1.x.x for Windows 7 and 8. Ver 2.x.x for Windows 10 and 11. 

### How to use
Right click any image and click the **Open with Fly** menu item to see the App. In Windows 7,8 and 10 this menu will be accessible on right click itself. With Windows 11, sometimes we may need to press **Show more options** to see the **Open with Fly** menu. With regular usage in Windows 11, the **Open with Fly** menu will get promoted to the main menu.

![](https://github.com/riyasy/FlyPhotos/blob/main/Misc/ImagesForDocumentation/02RightClickMenu.png?raw=true)

Click the < or > buttons or keyboard arrow keys to navigate. After cache is created long press these buttons to fly through the list of images.
CTRL + scroll on image to navigate to next or previous photo.
Scroll after placing mouse cursor on < or > key on screen to get next or previous photo.





